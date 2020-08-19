﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using squittal.ScrimPlanetmans.Data;
using squittal.ScrimPlanetmans.ScrimMatch.Messages;
using squittal.ScrimPlanetmans.ScrimMatch.Models;
using squittal.ScrimPlanetmans.Services.Planetside;
using squittal.ScrimPlanetmans.Services.Rulesets;
using squittal.ScrimPlanetmans.Services.ScrimMatch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace squittal.ScrimPlanetmans.ScrimMatch
{
    public class ScrimRulesetManager : IScrimRulesetManager
    {
        private readonly IDbContextHelper _dbContextHelper;
        private readonly IItemCategoryService _itemCategoryService;
        private readonly IRulesetDataService _rulesetDataService;
        private readonly IScrimMessageBroadcastService _messageService;
        public ILogger<ScrimRulesetManager> _logger;

        public Ruleset ActiveRuleset { get; private set; }

        private readonly int _defaultRulesetId = 1;


        public ScrimRulesetManager(IDbContextHelper dbContextHelper, IItemCategoryService itemCategoryService, IRulesetDataService rulesetDataService, IScrimMessageBroadcastService messageService, ILogger<ScrimRulesetManager> logger)
        {
            _dbContextHelper = dbContextHelper;
            _itemCategoryService = itemCategoryService;
            _rulesetDataService = rulesetDataService;
            _messageService = messageService;
            _logger = logger;
        }

        public async Task<IEnumerable<Ruleset>> GetRulesetsAsync(CancellationToken cancellationToken)
        {
            return await _rulesetDataService.GetAllRulesetsAsync(cancellationToken);
        }

        public async Task<Ruleset> GetActiveRulesetAsync(bool forceRefresh = false)
        {
            if (ActiveRuleset == null)
            {
                return await GetDefaultRuleset();
            }
            else if (forceRefresh || ActiveRuleset.RulesetActionRules == null || !ActiveRuleset.RulesetActionRules.Any() || ActiveRuleset.RulesetItemCategoryRules == null || !ActiveRuleset.RulesetItemCategoryRules.Any())
            {
                await SetupActiveRuleset();
                return ActiveRuleset;
            }
            else
            {
                return ActiveRuleset;
            }
        }


        public async Task<Ruleset> ActivateRulesetAsync(int rulesetId)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var currentActiveRuleset = await dbContext.Rulesets.FirstOrDefaultAsync(r => r.IsActive == true);

                var newActiveRuleset = await _rulesetDataService.ActivateRulesetAsync(rulesetId);

                if (newActiveRuleset == null)
                {
                    return null;
                }

                ActiveRuleset = newActiveRuleset;

                var message = new ActiveRulesetChangeMessage(ActiveRuleset, currentActiveRuleset);
                _messageService.BroadcastActiveRulesetChangeMessage(message);

                return ActiveRuleset;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
                return null;
            }
        }


        public async Task SetupActiveRuleset()
        {
            using var factory = _dbContextHelper.GetFactory();
            var dbContext = factory.GetDbContext();

            var ruleset = await dbContext.Rulesets.FirstOrDefaultAsync(r => r.IsActive == true);

            if (ruleset == null)
            {
                _logger.LogError($"Failed to setup active ruleset: no ruleset found");
                return;
            }

            ActiveRuleset = await _rulesetDataService.GetRulesetFromIdAsync(ruleset.Id, CancellationToken.None);

            _logger.LogInformation($"Active ruleset loaded: {ActiveRuleset.Name}");
        }

        public async Task<Ruleset> GetDefaultRuleset()
        {
            using var factory = _dbContextHelper.GetFactory();
            var dbContext = factory.GetDbContext();

            var ruleset = await dbContext.Rulesets.FirstOrDefaultAsync(r => r.Id == _defaultRulesetId);

            if (ruleset == null)
            {
                return null;
            }

            ruleset = await _rulesetDataService.GetRulesetFromIdAsync(ruleset.Id, CancellationToken.None);

            return ruleset;
        }

        public async Task SeedDefaultRuleset()
        {
            using (var factory = _dbContextHelper.GetFactory())
            {
                var dbContext = factory.GetDbContext();

                var defaultRulesetId = _defaultRulesetId;

                var storeRuleset = await dbContext.Rulesets.FirstOrDefaultAsync(r => r.Id == defaultRulesetId);

                bool rulesetExistsInDb = false;

                var storeActionRules = new List<RulesetActionRule>();
                var storeItemCategoryRules = new List<RulesetItemCategoryRule>();
                var storeFacilityRules = new List<RulesetFacilityRule>();

                if (storeRuleset != null)
                {
                    storeActionRules = await dbContext.RulesetActionRules.Where(r => r.RulesetId == storeRuleset.Id).ToListAsync();
                    
                    storeItemCategoryRules = await dbContext.RulesetItemCategoryRules.Where(r => r.RulesetId == storeRuleset.Id).ToListAsync();

                    storeFacilityRules = await dbContext.RulesetFacilityRules.Where(r => r.RulesetId == storeRuleset.Id).ToListAsync();

                    rulesetExistsInDb = true;
                }
                else
                {
                    var utcNow = DateTime.UtcNow;
                    var newRuleset = new Ruleset
                    {
                        Name = "Default",
                        DateCreated = utcNow
                    };

                    storeRuleset = newRuleset;
                }


                storeRuleset.IsDefault = true;

                var activeRuleset = await dbContext.Rulesets.FirstOrDefaultAsync(r => r.IsActive == true);
                if (activeRuleset == null || activeRuleset.Id == defaultRulesetId)
                {
                    storeRuleset.IsActive = true;
                }


                #region Action rules
                var defaultActionRules = GetDefaultActionRules();
                var createdActionRules = new List<RulesetActionRule>();
                var allActionRules = new List<RulesetActionRule>();

                var allActionEnumValues = GetScrimActionTypes().Where(a => a != ScrimActionType.None && a != ScrimActionType.Login && a != ScrimActionType.Logout);

                var allActionValues = new List<ScrimActionType>();
                allActionValues.AddRange(allActionEnumValues);
                allActionValues.AddRange(storeActionRules.Select(ar => ar.ScrimActionType).Where(a => !allActionValues.Contains(a)).ToList());

                foreach (var actionType in allActionValues)
                {
                    var storeEntity = storeActionRules?.FirstOrDefault(r => r.ScrimActionType == actionType);
                    var defaultEntity = defaultActionRules.FirstOrDefault(r => r.ScrimActionType == actionType);

                    var isValidAction = storeEntity == null || allActionEnumValues.Any(enumValue => enumValue == storeEntity.ScrimActionType);

                    if (storeEntity == null)
                    {
                        if (defaultEntity != null) {
                            defaultEntity.RulesetId = defaultRulesetId;
                            createdActionRules.Add(defaultEntity);
                            allActionRules.Add(defaultEntity);
                        }
                        else
                        {
                            var newEntity = BuildRulesetActionRule(defaultRulesetId, actionType, 0);
                            createdActionRules.Add(newEntity);
                            allActionRules.Add(newEntity);
                        }
                    }
                    else if (isValidAction)
                    {
                        if (defaultEntity != null)
                        {
                            storeEntity.Points = defaultEntity.Points;
                            storeEntity.DeferToItemCategoryRules = defaultEntity.DeferToItemCategoryRules;
                            storeEntity.ScrimActionTypeDomain = defaultEntity.ScrimActionTypeDomain;
                        }
                        else
                        {
                            storeEntity.Points = 0;
                            storeEntity.ScrimActionTypeDomain = ScrimAction.GetDomainFromActionType(storeEntity.ScrimActionType);
                        }

                        dbContext.RulesetActionRules.Update(storeEntity);
                        allActionRules.Add(storeEntity);
                    }
                    else
                    {
                        dbContext.RulesetActionRules.Remove(storeEntity);
                    }
                }

                if (createdActionRules.Any())
                {
                    await dbContext.RulesetActionRules.AddRangeAsync(createdActionRules);
                }
                #endregion Action rules

                #region Item Category Rules
                var defaultItemCategoryRules = GetDefaultItemCategoryRules();
                var createdItemCategoryRules = new List<RulesetItemCategoryRule>();
                var allItemCategoryIds = await _itemCategoryService.GetItemCategoryIdsAsync();
                var allWeaponItemCategoryIds = await _itemCategoryService.GetWeaponItemCategoryIdsAsync();

                var allItemCategoryRules = new List<RulesetItemCategoryRule>();

                foreach (var categoryId in allItemCategoryIds)
                {
                    var isWeaponItemCategoryId = (allWeaponItemCategoryIds.Contains(categoryId)); 
                    
                    var storeEntity = storeItemCategoryRules?.FirstOrDefault(r => r.ItemCategoryId == categoryId);
                    var defaultEntity = defaultItemCategoryRules.FirstOrDefault(r => r.ItemCategoryId == categoryId);

                    if (storeEntity == null)
                    {
                        if (defaultEntity != null) {
                            defaultEntity.RulesetId = defaultRulesetId;
                            
                            createdItemCategoryRules.Add(defaultEntity);
                            allItemCategoryRules.Add(defaultEntity);
                        }
                        else if (isWeaponItemCategoryId)
                        {
                            var newEntity = BuildRulesetItemCategoryRule(defaultRulesetId, categoryId, 0);
                            createdItemCategoryRules.Add(newEntity);
                            allItemCategoryRules.Add(newEntity);

                        }
                    }
                    else
                    {
                        if (isWeaponItemCategoryId)
                        {
                            storeEntity.Points = defaultEntity != null ? defaultEntity.Points : 0;

                            dbContext.RulesetItemCategoryRules.Update(storeEntity);
                            allItemCategoryRules.Add(storeEntity);
                        }
                        else
                        {
                            dbContext.RulesetItemCategoryRules.Remove(storeEntity);
                        }
                    }
                }

                if (createdItemCategoryRules.Any())
                {
                    await dbContext.RulesetItemCategoryRules.AddRangeAsync(createdItemCategoryRules);
                }
                #endregion Item Category Rules

                #region Facility Rules
                var defaultFacilityRules = GetDefaultFacilityRules();

                var createdFacilityRules = new List<RulesetFacilityRule>();

                var allFacilityRules = new List<RulesetFacilityRule>(storeFacilityRules);
                allFacilityRules.AddRange(defaultFacilityRules);

                foreach (var facilityRule in allFacilityRules)
                {
                    var storeEntity = storeFacilityRules?.FirstOrDefault(r => r.FacilityId == facilityRule.FacilityId);
                    var defaultEntity = defaultFacilityRules.FirstOrDefault(r => r.FacilityId == facilityRule.FacilityId);

                    if (storeEntity == null)
                    {
                        if (defaultEntity != null)
                        {
                            defaultEntity.RulesetId = defaultRulesetId;

                            createdFacilityRules.Add(defaultEntity);
                            
                        }
                    }
                    else
                    {
                        if (defaultEntity != null)
                        {
                            storeEntity.FacilityId = defaultEntity.FacilityId;
                            dbContext.RulesetFacilityRules.Update(storeEntity);
                        }
                        else
                        {
                            dbContext.RulesetFacilityRules.Remove(storeEntity);
                            allFacilityRules.Remove(storeEntity);
                        }
                    }
                }

                if (createdFacilityRules.Any())
                {
                    await dbContext.RulesetFacilityRules.AddRangeAsync(createdFacilityRules);
                }
                #endregion Facility Rules


                storeRuleset.RulesetActionRules = allActionRules;
                storeRuleset.RulesetItemCategoryRules = allItemCategoryRules;
                storeRuleset.RulesetFacilityRules = allFacilityRules;

                if (rulesetExistsInDb)
                {
                    dbContext.Rulesets.Update(storeRuleset);
                }
                else
                {
                    dbContext.Rulesets.Add(storeRuleset);
                }

                await dbContext.SaveChangesAsync();

                ActiveRuleset = storeRuleset;
                ActiveRuleset.RulesetActionRules = allActionRules.ToList();
                ActiveRuleset.RulesetItemCategoryRules = allItemCategoryRules.ToList();
                ActiveRuleset.RulesetFacilityRules = allFacilityRules.ToList();
            }
        }

        private IEnumerable<RulesetItemCategoryRule> GetDefaultItemCategoryRules()
        {
            return new RulesetItemCategoryRule[]
            {
                BuildRulesetItemCategoryRule(2, 1),   // Knife
                BuildRulesetItemCategoryRule(3, 1),   // Pistol
                BuildRulesetItemCategoryRule(5, 1),   // SMG
                BuildRulesetItemCategoryRule(6, 1),   // LMG
                BuildRulesetItemCategoryRule(7, 1),   // Assault Rifle
                BuildRulesetItemCategoryRule(8, 1),   // Carbine
                BuildRulesetItemCategoryRule(11, 1),  // Sniper Rifle
                BuildRulesetItemCategoryRule(12, 1),  // Scout Rifle
                BuildRulesetItemCategoryRule(19, 1),  // Battle Rifle
                BuildRulesetItemCategoryRule(24, 1),  // Crossbow
                BuildRulesetItemCategoryRule(100, 1), // Infantry
                BuildRulesetItemCategoryRule(102, 1), // Infantry Weapons
                BuildRulesetItemCategoryRule(157, 1)  // Hybrid Rifle
            };
        }

        private IEnumerable<RulesetActionRule> GetDefaultActionRules()
        {
            // MaxKillInfantry & MaxKillMax are worth 0 points
            return new RulesetActionRule[]
            {
                BuildRulesetActionRule(ScrimActionType.FirstBaseCapture, 9), // PIL 1: 18
                BuildRulesetActionRule(ScrimActionType.SubsequentBaseCapture, 18), // PIL 1: 36 
                BuildRulesetActionRule(ScrimActionType.InfantryKillMax, 6), // PIL 1: -12
                BuildRulesetActionRule(ScrimActionType.InfantryTeamkillInfantry, -2), // PIL 1: -3
                BuildRulesetActionRule(ScrimActionType.InfantryTeamkillMax, -8), // PIL 1: -15
                BuildRulesetActionRule(ScrimActionType.InfantrySuicide, -2), // PIL 1: -3
                BuildRulesetActionRule(ScrimActionType.MaxTeamkillMax, -8), // PIL 1: -15
                BuildRulesetActionRule(ScrimActionType.MaxTeamkillInfantry, -2), // PIL 1: -3
                BuildRulesetActionRule(ScrimActionType.MaxSuicide, -8), // PIL 1: -12
                BuildRulesetActionRule(ScrimActionType.MaxKillInfantry, 0), // PIL 1: 0
                BuildRulesetActionRule(ScrimActionType.MaxKillMax, 0), // PIL 1: 0
                BuildRulesetActionRule(ScrimActionType.InfantryKillInfantry, 0, true) // PIL 1: 0
            };
        }

        private IEnumerable<RulesetFacilityRule> GetDefaultFacilityRules()
        {
            return new RulesetFacilityRule[]
            {
                /* Hossin */
                BuildRulesetFacilityRule(266000), // Kessel's Crossing
                BuildRulesetFacilityRule(272000), // Bridgewater Shipping
                BuildRulesetFacilityRule(283000), // Nettlemire
                BuildRulesetFacilityRule(286000), // Four Fingers
                BuildRulesetFacilityRule(287070), // Fort Liberty
                BuildRulesetFacilityRule(302030), // Acan South
                BuildRulesetFacilityRule(303030), // Bitol Eastern
                BuildRulesetFacilityRule(305010), // Ghanan South
                BuildRulesetFacilityRule(307010), // Chac Fusion
                
                /* Esamir */
                BuildRulesetFacilityRule(239000), // Pale Canyon
                BuildRulesetFacilityRule(244610), // Rime Analtyics
                BuildRulesetFacilityRule(244620), // The Rink
                BuildRulesetFacilityRule(252020), // Elli Barracks
                BuildRulesetFacilityRule(254010), // Eisa Mountain Pass
                
                /* Indar */
                BuildRulesetFacilityRule(219), // Ceres
                BuildRulesetFacilityRule(230), // Xenotech
                BuildRulesetFacilityRule(3430), // Peris Eastern
                BuildRulesetFacilityRule(3620), // Rashnu
                
                /* Amerish */
                BuildRulesetFacilityRule(210002) // Wokuk Shipping
            };
        }

        private RulesetActionRule BuildRulesetActionRule(int rulesetId, ScrimActionType actionType, int points = 0, bool deferToItemCategoryRules = false)
        {
            return new RulesetActionRule
            {
                RulesetId = rulesetId,
                ScrimActionType = actionType,
                Points = points,
                DeferToItemCategoryRules = deferToItemCategoryRules,
                ScrimActionTypeDomain = ScrimAction.GetDomainFromActionType(actionType)
            };
        }

        private RulesetActionRule BuildRulesetActionRule(ScrimActionType actionType, int points = 0, bool deferToItemCategoryRules = false)
        {
            return new RulesetActionRule
            {
                ScrimActionType = actionType,
                Points = points,
                DeferToItemCategoryRules = deferToItemCategoryRules,
                ScrimActionTypeDomain = ScrimAction.GetDomainFromActionType(actionType)
            };
        }

        private RulesetItemCategoryRule BuildRulesetItemCategoryRule(int rulesetId, int itemCategoryId, int points = 0)
        {
            return new RulesetItemCategoryRule
            {
                RulesetId = rulesetId,
                ItemCategoryId = itemCategoryId,
                Points = points
            };
        }

        private RulesetItemCategoryRule BuildRulesetItemCategoryRule(int itemCategoryId, int points = 0)
        {
            return new RulesetItemCategoryRule
            {
                ItemCategoryId = itemCategoryId,
                Points = points
            };
        }

        private RulesetFacilityRule BuildRulesetFacilityRule(int rulesetId, int facilityId)
        {
            return new RulesetFacilityRule
            {
                RulesetId = rulesetId,
                FacilityId = facilityId
            };
        }
        private RulesetFacilityRule BuildRulesetFacilityRule(int facilityId)
        {
            return new RulesetFacilityRule
            {
                FacilityId = facilityId
            };
        }

        public async Task SeedScrimActionModels()
        {
            using var factory = _dbContextHelper.GetFactory();
            var dbContext = factory.GetDbContext();

            var createdEntities = new List<ScrimAction>();

            var allActionTypeValues = new List<ScrimActionType>();

            var enumValues = (ScrimActionType[])Enum.GetValues(typeof(ScrimActionType));

            allActionTypeValues.AddRange(enumValues);

            var storeEntities = await dbContext.ScrimActions.ToListAsync();

            allActionTypeValues.AddRange(storeEntities.Where(a => !allActionTypeValues.Contains(a.Action)).Select(a => a.Action).ToList());

            allActionTypeValues.Distinct().ToList();

            foreach (var value in allActionTypeValues)
            {
                try
                {

                    var storeEntity = storeEntities.FirstOrDefault(e => e.Action == value);
                    var isValidEnum = enumValues.Any(enumValue => enumValue == value);

                    if (storeEntity == null)
                    {
                        createdEntities.Add(ConvertToDbModel(value));
                    }
                    else if (isValidEnum)
                    {
                        storeEntity = ConvertToDbModel(value);
                        dbContext.ScrimActions.Update(storeEntity);
                    }
                    else
                    {
                        dbContext.ScrimActions.Remove(storeEntity);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString());
                }
            }

            if (createdEntities.Any())
            {
                await dbContext.ScrimActions.AddRangeAsync(createdEntities);
            }

            await dbContext.SaveChangesAsync();

            _logger.LogInformation($"Seeded Scrim Actions store");
        }

        private ScrimAction ConvertToDbModel(ScrimActionType value)
        {
            var name = Enum.GetName(typeof(ScrimActionType), value);

            return new ScrimAction
            {
                Action = value,
                Name = name,
                Description = Regex.Replace(name, @"(\p{Ll})(\p{Lu})", "$1 $2"),
                Domain = ScrimAction.GetDomainFromActionType(value)
            };
        }

        public IEnumerable<ScrimActionType> GetScrimActionTypes()
        {
            return (ScrimActionType[])Enum.GetValues(typeof(ScrimActionType));
        }
    }
}
