﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using squittal.ScrimPlanetmans.Data;
using squittal.ScrimPlanetmans.Logging;
using squittal.ScrimPlanetmans.Models;
using squittal.ScrimPlanetmans.Models.Forms;
using squittal.ScrimPlanetmans.Models.Planetside;
using squittal.ScrimPlanetmans.ScrimMatch.Messages;
using squittal.ScrimPlanetmans.ScrimMatch.Models;
using squittal.ScrimPlanetmans.Services.Planetside;
using squittal.ScrimPlanetmans.Services.ScrimMatch;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace squittal.ScrimPlanetmans.Services.Rulesets
{
    public class RulesetDataService : IRulesetDataService
    {
        private readonly IDbContextHelper _dbContextHelper;
        private readonly IFacilityService _facilityService;
        private readonly IScrimMessageBroadcastService _messageService;
        private readonly ILogger<RulesetDataService> _logger;

        private ConcurrentDictionary<int, Ruleset> RulesetsMap { get; set; } = new ConcurrentDictionary<int, Ruleset>();

        public int ActiveRulesetId { get; private set; }

        public int CustomDefaultRulesetId { get; private set; }

        private readonly int _rulesetBrowserPageSize = 15;

        public int DefaultRulesetId { get; } = 1;

        private readonly KeyedSemaphoreSlim _rulesetLock = new KeyedSemaphoreSlim();
        private readonly KeyedSemaphoreSlim _actionRulesLock = new KeyedSemaphoreSlim();
        private readonly KeyedSemaphoreSlim _itemCategoryRulesLock = new KeyedSemaphoreSlim();
        private readonly KeyedSemaphoreSlim _facilityRulesLock = new KeyedSemaphoreSlim();

        private readonly KeyedSemaphoreSlim _rulesetExportLock = new KeyedSemaphoreSlim();

        public AutoResetEvent _defaultRulesetAutoEvent = new AutoResetEvent(true);

        public static Regex RulesetNameRegex { get; } = new Regex("^([A-Za-z0-9()\\[\\]\\-_'.][ ]{0,1}){1,49}[A-Za-z0-9()\\[\\]\\-_'.]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static Regex RulesetDefaultMatchTitleRegex => RulesetNameRegex; //(^(?!.)$|^([A-Za-z0-9()\[\]\-_'.][ ]{0,1}){1,49}[A-Za-z0-9()\[\]\-_'.]$)

        public RulesetDataService(IDbContextHelper dbContextHelper, IFacilityService facilityService, IScrimMessageBroadcastService messageService, ILogger<RulesetDataService> logger)
        {
            _dbContextHelper = dbContextHelper;
            _facilityService = facilityService;
            _messageService = messageService;
            _logger = logger;
        }

        #region GET methods
        public async Task<PaginatedList<Ruleset>> GetRulesetListAsync(int? pageIndex, CancellationToken cancellationToken)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var rulesetsQuery = dbContext.Rulesets
                                                .AsQueryable()
                                                .AsNoTracking()
                                                .OrderByDescending(r => r.Id == ActiveRulesetId)
                                                .ThenByDescending(r => r.IsCustomDefault)
                                                .ThenByDescending(r => r.IsDefault)
                                                .ThenBy(r => r.Name);

                var paginatedList = await PaginatedList<Ruleset>.CreateAsync(rulesetsQuery, pageIndex ?? 1, _rulesetBrowserPageSize, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (paginatedList == null)
                {
                    return null;
                }

                return paginatedList;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetsAsync page {pageIndex}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetsAsync page {pageIndex}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }
        
        public  async Task<IEnumerable<Ruleset>> GetAllRulesetsAsync(CancellationToken cancellationToken)
        {
            if (RulesetsMap.Count == 0 || !RulesetsMap.Any())
            {
                await SetUpRulesetsMapAsync(cancellationToken);
            }

            if (RulesetsMap  == null || !RulesetsMap.Any())
            {
                return null;
            }

            return RulesetsMap.Values.ToList();
        }

        public async Task<Ruleset> GetRulesetFromIdAsync(int rulesetId, CancellationToken cancellationToken, bool includeCollections = true)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                Ruleset ruleset;

                ruleset = await dbContext.Rulesets
                                               .Where(r => r.Id == rulesetId)
                                               .FirstOrDefaultAsync(cancellationToken);

                if (includeCollections)
                {
                    var TaskList = new List<Task>();

                    var actionRulesTask = GetRulesetActionRulesAsync(rulesetId, CancellationToken.None);
                    TaskList.Add(actionRulesTask);

                    var itemCategoryRulesTask = GetRulesetItemCategoryRulesAsync(rulesetId, CancellationToken.None);
                    TaskList.Add(itemCategoryRulesTask);

                    var facilityRulesTask = GetRulesetFacilityRulesAsync(rulesetId, CancellationToken.None);
                    TaskList.Add(facilityRulesTask);

                    await Task.WhenAll(TaskList);

                    ruleset.RulesetActionRules = actionRulesTask.Result.ToList();
                    ruleset.RulesetItemCategoryRules = itemCategoryRulesTask.Result.ToList();
                    ruleset.RulesetFacilityRules = facilityRulesTask.Result.ToList();
                }

                cancellationToken.ThrowIfCancellationRequested();

                return ruleset;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetFromIdAsync rulesetId {rulesetId}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetFromIdAsync rulesetId {rulesetId}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }

        public async Task<Ruleset> GetRulesetWithFacilityRules(int rulesetId, CancellationToken cancellationToken)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                Ruleset ruleset;

                    ruleset = await dbContext.Rulesets
                                                .Where(r => r.Id == rulesetId)
                                                .Include("RulesetFacilityRules")
                                                .Include("RulesetFacilityRules.MapRegion")
                                                .FirstOrDefaultAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                return ruleset;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetWithFacilityRules rulesetId {rulesetId}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetWithFacilityRules rulesetId {rulesetId}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }

        public async Task<IEnumerable<RulesetActionRule>> GetRulesetActionRulesAsync(int rulesetId, CancellationToken cancellationToken)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var rules = await dbContext.RulesetActionRules
                                               .Where(r => r.RulesetId == rulesetId)
                                               .ToListAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                return rules;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetActionRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetActionRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }

        public async Task<IEnumerable<RulesetItemCategoryRule>> GetRulesetItemCategoryRulesAsync(int rulesetId, CancellationToken cancellationToken)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var rules = await dbContext.RulesetItemCategoryRules
                                               .Where(r => r.RulesetId == rulesetId)
                                               .Include("ItemCategory")
                                               .OrderBy(r => r.ItemCategory.Domain)
                                               .ThenBy(r => r.ItemCategory.Name)
                                               .ToListAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                return rules;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetItemCategoryRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetItemCategoryRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }

        public async Task<IEnumerable<RulesetFacilityRule>> GetRulesetFacilityRulesAsync(int rulesetId, CancellationToken cancellationToken)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var rules = await dbContext.RulesetFacilityRules
                                               .Where(r => r.RulesetId == rulesetId)
                                               .Include("MapRegion")
                                               .OrderBy(r => r.MapRegion.ZoneId)
                                               .ThenBy(r => r.MapRegion.FacilityName)
                                               .ToListAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                return rules;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetFacilityRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetFacilityRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }

        public async Task<IEnumerable<RulesetFacilityRule>> GetUnusedRulesetFacilityRulesAsync(int rulesetId, CancellationToken cancellationToken)
        {
            try
            {
                var usedFacilities = await GetRulesetFacilityRulesAsync(rulesetId, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var allFacilities = await _facilityService.GetScrimmableMapRegionsAsync();
                
                if (usedFacilities == null)
                {
                    return allFacilities?.Select(a => ConvertToFacilityRule(rulesetId, a)).ToList();
                }

                return allFacilities?.Where(a => !usedFacilities.Any(u => u.FacilityId == a.FacilityId))
                                        .Select(a => ConvertToFacilityRule(rulesetId, a))
                                        .OrderBy(r => r.MapRegion.ZoneId)
                                        .ThenBy(r => r.MapRegion.FacilityName)
                                        .ToList();
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"Task Request cancelled: GetRulesetFacilityRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Request cancelled: GetRulesetFacilityRulesAsync rulesetId {rulesetId}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex}");

                return null;
            }
        }

        private RulesetFacilityRule ConvertToFacilityRule(int rulesetId, MapRegion mapRegion)
        {
            return new RulesetFacilityRule
            {
                RulesetId = rulesetId,
                FacilityId = mapRegion.FacilityId,
                MapRegionId = mapRegion.Id,
                MapRegion = mapRegion
            };
        }
        #endregion GET methods


        #region SAVE / UPDATE methods
        public async Task<bool> UpdateRulesetInfo(Ruleset rulesetUpdate, CancellationToken cancellationToken)
        {
            var updateId = rulesetUpdate.Id;
            
            if (updateId == DefaultRulesetId || rulesetUpdate.IsDefault)
            {
                return false;
            }
            
            var updateName = rulesetUpdate.Name;
            var updateRoundLength = rulesetUpdate.DefaultRoundLength;
            var updateMatchTitle = rulesetUpdate.DefaultMatchTitle;

            if (!IsValidRulesetName(updateName))
            {
                _logger.LogError($"Error updating Ruleset {updateId} info: invalid ruleset name");
                return false;
            }

            if (!IsValidRulesetDefaultRoundLength(updateRoundLength))
            {
                _logger.LogError($"Error updating Ruleset {updateId} info: invalid default round length");
                return false;
            }

            if (!IsValidRulesetDefaultMatchTitle(updateMatchTitle))
            {
                _logger.LogError($"Error updating Ruleset {updateId} info: invalid default match title");
            }

            using (await _rulesetLock.WaitAsync($"{updateId}"))
            {
                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeEntity = await GetRulesetFromIdAsync(updateId, cancellationToken, false);

                    if (storeEntity == null)
                    {
                        return false;
                    }

                    var oldName = storeEntity.Name;
                    var oldIsHidden = storeEntity.DefaultRoundLength;

                    storeEntity.Name = updateName;
                    storeEntity.DefaultRoundLength = updateRoundLength;
                    storeEntity.DefaultMatchTitle = updateMatchTitle;
                    storeEntity.DateLastModified = DateTime.UtcNow;

                    dbContext.Rulesets.Update(storeEntity);

                    await dbContext.SaveChangesAsync();

                    await SetUpRulesetsMapAsync(cancellationToken);

                    // TODO: broadcast Ruleset Info Change Message

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error updating Ruleset {updateId} info: {ex}");
                    return false;
                }
            }
        }

        /*
         * Upsert New or Modified RulesetActionRules for a specific ruleset.
         */
        public async Task SaveRulesetActionRules(int rulesetId, IEnumerable<RulesetActionRule> rules)
        {
            if (rulesetId == DefaultRulesetId)
            {
                return;
            }
            
            using (await _actionRulesLock.WaitAsync($"{rulesetId}"))
            {
                var ruleUpdates = rules.Where(rule => rule.RulesetId == rulesetId).ToList();

                if (!ruleUpdates.Any())
                {
                    return;
                }

                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRules = await dbContext.RulesetActionRules.Where(rule => rule.RulesetId == rulesetId).ToListAsync();

                    var newEntities = new List<RulesetActionRule>();

                    foreach (var rule in ruleUpdates)
                    {
                        var storeEntity = storeRules.Where(r => r.ScrimActionType == rule.ScrimActionType).FirstOrDefault();

                        if (storeEntity == null)
                        {
                            newEntities.Add(rule);
                        }
                        else
                        {
                            storeEntity = rule;
                            dbContext.RulesetActionRules.Update(storeEntity);
                        }
                    }

                    if (newEntities.Any())
                    {
                        dbContext.RulesetActionRules.AddRange(newEntities);
                    }

                    var storeRuleset = await dbContext.Rulesets.Where(r => r.Id == rulesetId).FirstOrDefaultAsync();
                    if (storeRuleset != null)
                    {
                        storeRuleset.DateLastModified = DateTime.UtcNow;
                        dbContext.Rulesets.Update(storeRuleset);
                    }

                    await dbContext.SaveChangesAsync();

                    var message = new RulesetRuleChangeMessage(storeRuleset, RulesetRuleChangeType.ActionRule);
                    _messageService.BroadcastRulesetRuleChangeMessage(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error saving RulesetActionRule changes to database: {ex}");
                }
            }
        }

        /*
         * Upsert New or Modified RulesetItemCategoryRules for a specific ruleset.
         */
        public async Task SaveRulesetItemCategoryRules(int rulesetId, IEnumerable<RulesetItemCategoryRule> rules)
        {
            if (rulesetId == DefaultRulesetId)
            {
                return;
            }

            using (await _itemCategoryRulesLock.WaitAsync($"{rulesetId}"))
            {
                var ruleUpdates = rules.Where(rule => rule.RulesetId == rulesetId).ToList();

                if (!ruleUpdates.Any())
                {
                    return;
                }
            
                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRules = await dbContext.RulesetItemCategoryRules.Where(rule => rule.RulesetId == rulesetId).ToListAsync();

                    var newEntities = new List<RulesetItemCategoryRule>();

                    foreach (var rule in ruleUpdates)
                    {
                        var storeEntity = storeRules.Where(r => r.ItemCategoryId == rule.ItemCategoryId).FirstOrDefault();

                        if (storeEntity == null)
                        {
                            newEntities.Add(rule);
                        }
                        else
                        {
                            storeEntity = rule;
                            dbContext.RulesetItemCategoryRules.Update(storeEntity);
                        }
                    }

                    if (newEntities.Any())
                    {
                        dbContext.RulesetItemCategoryRules.AddRange(newEntities);
                    }

                    var storeRuleset = await dbContext.Rulesets.Where(r => r.Id == rulesetId).FirstOrDefaultAsync();
                    if (storeRuleset != null)
                    {
                        storeRuleset.DateLastModified = DateTime.UtcNow;
                        dbContext.Rulesets.Update(storeRuleset);
                    }

                    await dbContext.SaveChangesAsync();

                    var message = new RulesetRuleChangeMessage(storeRuleset, RulesetRuleChangeType.ItemCategoryRule);
                    _messageService.BroadcastRulesetRuleChangeMessage(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error saving RulesetItemCategoryRule changes to database: {ex}");
                }
            }
        }

        private async Task SaveRulesetFacilityRules(int rulesetId, IEnumerable<RulesetFacilityRule> rules)
        {
            if (rulesetId == DefaultRulesetId)
            {
                return;
            }

            using (await _facilityRulesLock.WaitAsync($"{rulesetId}"))
            {
                var ruleUpdates = rules.Where(rule => rule.RulesetId == rulesetId).ToList();

                if (!ruleUpdates.Any())
                {
                    return;
                }

                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRules = await dbContext.RulesetFacilityRules.Where(rule => rule.RulesetId == rulesetId).ToListAsync();
                    var allRules = new List<RulesetFacilityRule>(storeRules);
                    allRules.AddRange(ruleUpdates);

                    var newEntities = new List<RulesetFacilityRule>();

                    foreach (var rule in allRules)
                    {
                        var storeEntity = storeRules.Where(r => r.FacilityId == rule.FacilityId).FirstOrDefault();
                        var updateRule = ruleUpdates.Where(r => r.FacilityId == rule.FacilityId).FirstOrDefault();

                        if (storeEntity == null)
                        {
                            newEntities.Add(rule);
                        }
                        else if (updateRule == null)
                        {
                            dbContext.RulesetFacilityRules.Remove(storeEntity);
                        }
                        else
                        {
                            storeEntity = updateRule;
                            dbContext.RulesetFacilityRules.Update(storeEntity);
                        }
                    }

                    if (newEntities.Any())
                    {
                        dbContext.RulesetFacilityRules.AddRange(newEntities);
                    }

                    //if (!isRulesetCreation)
                    //{

                    var storeRuleset = await dbContext.Rulesets.Where(r => r.Id == rulesetId).FirstOrDefaultAsync();
                    if (storeRuleset != null)
                    {
                        storeRuleset.DateLastModified = DateTime.UtcNow;
                        dbContext.Rulesets.Update(storeRuleset);
                    }
                    //}

                    await dbContext.SaveChangesAsync();

                    //if (!isRulesetCreation)
                    //{
                    var message = new RulesetRuleChangeMessage(storeRuleset, RulesetRuleChangeType.ItemCategoryRule);
                    _messageService.BroadcastRulesetRuleChangeMessage(message);
                    //}
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error saving SaveRulesetFacilityRules to database: {ex}");
                }
            }
        }

        /*
         * Upsert New or Modified RulesetFacilityRules for a specific ruleset.
         */
        public async Task SaveRulesetFacilityRules(int rulesetId, IEnumerable<RulesetFacilityRuleChange> rules)
        {
            if (rulesetId == DefaultRulesetId)
            {
                return;
            }

            using (await _facilityRulesLock.WaitAsync($"{rulesetId}"))
            {
                var ruleUpdates = rules.Where(rule => rule.RulesetFacilityRule.RulesetId == rulesetId).ToList();

                if (!ruleUpdates.Any())
                {
                    return;
                }

                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRules = await dbContext.RulesetFacilityRules.Where(rule => rule.RulesetId == rulesetId).ToListAsync();

                    var newEntities = new List<RulesetFacilityRule>();

                    foreach (var rule in ruleUpdates)
                    {
                        var storeEntity = storeRules.Where(r => r.FacilityId == rule.RulesetFacilityRule.FacilityId).FirstOrDefault();

                        if (storeEntity == null)
                        {
                            if (rule.ChangeType == RulesetFacilityRuleChangeType.Add)
                            {
                                rule.RulesetFacilityRule.MapRegion = null;
                                newEntities.Add(rule.RulesetFacilityRule);
                            }
                        }
                        else
                        {
                            if (rule.ChangeType == RulesetFacilityRuleChangeType.Add)
                            {

                                rule.RulesetFacilityRule.MapRegion = null;
                                storeEntity = rule.RulesetFacilityRule;
                                dbContext.RulesetFacilityRules.Update(storeEntity);
                            }
                            else if (rule.ChangeType == RulesetFacilityRuleChangeType.Remove)
                            {
                                dbContext.RulesetFacilityRules.Remove(storeEntity);
                            }
                        }
                    }

                    if (newEntities.Any())
                    {
                        dbContext.RulesetFacilityRules.AddRange(newEntities);
                    }

                    var storeRuleset = await dbContext.Rulesets.Where(r => r.Id == rulesetId).FirstOrDefaultAsync();
                    if (storeRuleset != null)
                    {
                        storeRuleset.DateLastModified = DateTime.UtcNow;
                        dbContext.Rulesets.Update(storeRuleset);
                    }

                    await dbContext.SaveChangesAsync();

                    var message = new RulesetRuleChangeMessage(storeRuleset, RulesetRuleChangeType.FacilityRule);
                    _messageService.BroadcastRulesetRuleChangeMessage(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error saving RulesetFacilityRule changes to database: {ex}");
                }
            }
        }

        public async Task<Ruleset> SaveNewRulesetAsync(Ruleset ruleset)
        {
            ruleset = await CreateRulesetAsync(ruleset);

            if (ruleset == null)
            {
                return null;
            }

            var TaskList = new List<Task>();

            var itemCategoryRulesTask = SeedNewRulesetDefaultItemCategoryRules(ruleset.Id);
            TaskList.Add(itemCategoryRulesTask);

            var actionRulesTask = SeedNewRulesetDefaultActionRules(ruleset.Id);
            TaskList.Add(actionRulesTask);

            var facilityRulesTask = SeedNewRulesetDefaultFacilityRules(ruleset.Id);
            TaskList.Add(facilityRulesTask);

            await Task.WhenAll(TaskList);

            return ruleset;
        }

        private async Task<Ruleset> CreateRulesetAsync(Ruleset ruleset)
        {
            if (!IsValidRulesetName(ruleset.Name) || ruleset.IsDefault
                || !IsValidRulesetDefaultRoundLength(ruleset.DefaultRoundLength) || !IsValidRulesetDefaultMatchTitle(ruleset.DefaultMatchTitle))
            {
                return null;
            }


            using (await _rulesetLock.WaitAsync($"{ruleset.Id}"))
            {
                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    if (ruleset.DateCreated == default(DateTime))
                    {
                        ruleset.DateCreated = DateTime.UtcNow;
                    }

                    dbContext.Rulesets.Add(ruleset);

                    await dbContext.SaveChangesAsync();

                    await SetUpRulesetsMapAsync(CancellationToken.None);

                    return ruleset;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString());

                    return null;
                }
            }
        }

        /*
         * Seed default RulesetItemCategoryRules for a newly created ruleset. Will not do anything if the ruleset
         * already has any RulesetItemCategoryRules in the database.
        */
        private async Task SeedNewRulesetDefaultActionRules(int rulesetId)
        {
            using (await _actionRulesLock.WaitAsync($"{rulesetId}"))
            {
                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRulesCount = await dbContext.RulesetActionRules.Where(r => r.RulesetId == rulesetId).CountAsync();

                    if (storeRulesCount > 0)
                    {
                        return;
                    }

                    var defaultRulesetId = await dbContext.Rulesets.Where(r => r.IsDefault).Select(r => r.Id).FirstOrDefaultAsync();

                    var defaultRules = await dbContext.RulesetActionRules.Where(r => r.RulesetId == defaultRulesetId).ToListAsync();

                    if (defaultRules == null)
                    {
                        return;
                    }

                    dbContext.RulesetActionRules.AddRange(defaultRules.Select(r => BuildRulesetActionRule(rulesetId, r.ScrimActionType, r.Points, r.DeferToItemCategoryRules)));

                    await dbContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString());

                    return;
                }
            }
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

        private async Task SeedNewRulesetDefaultItemCategoryRules(int rulesetId)
        {
            using (await _itemCategoryRulesLock.WaitAsync($"{rulesetId}"))
            {
                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRulesCount = await dbContext.RulesetItemCategoryRules.Where(r => r.RulesetId == rulesetId).CountAsync();

                    if (storeRulesCount > 0)
                    {
                        return;
                    }

                    var defaultRulesetId = await dbContext.Rulesets.Where(r => r.IsDefault).Select(r => r.Id).FirstOrDefaultAsync();

                    var defaultRules = await dbContext.RulesetItemCategoryRules.Where(r => r.RulesetId == defaultRulesetId).ToListAsync();

                    if (defaultRules == null)
                    {
                        return;
                    }

                    dbContext.RulesetItemCategoryRules.AddRange(defaultRules.Select(r => BuildRulesetItemCategoryRule(rulesetId, r.ItemCategoryId, r.Points)));

                    await dbContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString());

                    return;
                }
            }
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

        private async Task SeedNewRulesetDefaultFacilityRules(int rulesetId)
        {
            using (await _facilityRulesLock.WaitAsync($"{rulesetId}"))
            {
                try
                {
                    using var factory = _dbContextHelper.GetFactory();
                    var dbContext = factory.GetDbContext();

                    var storeRulesCount = await dbContext.RulesetFacilityRules.Where(r => r.RulesetId == rulesetId).CountAsync();

                    if (storeRulesCount > 0)
                    {
                        return;
                    }

                    var defaultRulesetId = await dbContext.Rulesets.Where(r => r.IsDefault).Select(r => r.Id).FirstOrDefaultAsync();

                    var defaultRules = await dbContext.RulesetFacilityRules.Where(r => r.RulesetId == defaultRulesetId).ToListAsync();

                    if (defaultRules == null)
                    {
                        return;
                    }

                    dbContext.RulesetFacilityRules.AddRange(defaultRules.Select(r => BuildRulesetFacilityRule(rulesetId, r.FacilityId, r.MapRegionId)));

                    await dbContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString());

                    return;
                }
            }
        }

        private RulesetFacilityRule BuildRulesetFacilityRule(int rulesetId, int facilityId, int mapRegionId)
        {
            return new RulesetFacilityRule
            {
                RulesetId = rulesetId,
                FacilityId = facilityId,
                MapRegionId = mapRegionId
            };
        }
        #endregion SAVE / UPDATE methods

        #region Helper Methods
        private async Task SetUpRulesetsMapAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var rulesets = await dbContext.Rulesets.ToListAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var newMap = new ConcurrentDictionary<int, Ruleset>();

                foreach (var rulesetId in RulesetsMap.Keys)
                {
                    if (!rulesets.Any(t => t.Id == rulesetId))
                    {
                        RulesetsMap.TryRemove(rulesetId, out var removeRuleset);
                    }
                }

                foreach (var ruleset in rulesets)
                {
                    RulesetsMap.AddOrUpdate(ruleset.Id, ruleset, (key, oldValue) => ruleset);
                }

                var customDefaultRuleset = rulesets.Where(r => r.IsCustomDefault).FirstOrDefault();
                CustomDefaultRulesetId = customDefaultRuleset != null ? customDefaultRuleset.Id : DefaultRulesetId;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed setting up RulesetsMap: {ex}");
            }
        }
        #endregion Helper Methods

        #region Ruleset Activation / Defaulting / Favoriting
        public void SetActiveRulesetId(int rulesetId)
        {
            ActiveRulesetId = rulesetId;
        }

        public async Task<Ruleset> SetCustomDefaultRulesetAsync(int rulesetId)
        {
            _defaultRulesetAutoEvent.WaitOne();

            try
            {
                using var factory = _dbContextHelper.GetFactory();
                var dbContext = factory.GetDbContext();

                var currentDefaultRuleset = await dbContext.Rulesets.FirstOrDefaultAsync(r => r.IsCustomDefault);

                var newDefaultRuleset = await GetRulesetFromIdAsync(rulesetId, CancellationToken.None, false);

                if (newDefaultRuleset == null)
                {
                    _defaultRulesetAutoEvent.Set();
                    return null;
                }
                
                
                if (currentDefaultRuleset == null)
                {
                    newDefaultRuleset.IsCustomDefault = true;
                    dbContext.Rulesets.Update(newDefaultRuleset);
                }
                else if (currentDefaultRuleset.Id != rulesetId)
                {
                    currentDefaultRuleset.IsCustomDefault = false;
                    dbContext.Rulesets.Update(currentDefaultRuleset);

                    newDefaultRuleset.IsCustomDefault = true;
                    dbContext.Rulesets.Update(newDefaultRuleset);
                }
                else
                {
                    _defaultRulesetAutoEvent.Set();

                    CustomDefaultRulesetId = currentDefaultRuleset.Id;

                    return currentDefaultRuleset;
                }

                await dbContext.SaveChangesAsync();

                _defaultRulesetAutoEvent.Set();

                _logger.LogInformation($"Set ruleset {rulesetId} as new custom default ruleset");

                CustomDefaultRulesetId = newDefaultRuleset.Id;

                return newDefaultRuleset;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed setting ruleset {rulesetId} as new custom default ruleset: {ex}");

                _defaultRulesetAutoEvent.Set();

                return null;
            }
        }
        #endregion Ruleset Activation / Defaulting / Favoriting

        #region Import / Export JSON
        public async Task<bool> ExportRulesetToJsonFile(int rulesetId, CancellationToken cancellationToken)
        {
            using (await _rulesetExportLock.WaitAsync($"{rulesetId}"))
            {
                try
                {
                    var ruleset = await GetRulesetFromIdAsync(rulesetId, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (ruleset == null)
                    {
                        return false;
                    }

                    var fileName = GetRulesetFileName(rulesetId, ruleset.Name);


                    if (await RulesetFileHandler.WriteToJsonFile(fileName, new JsonRuleset(ruleset, fileName)))
                    {
                        _logger.LogInformation($"Exported ruleset {rulesetId} to file {fileName}");

                        return true;
                    }
                    else
                    {
                        _logger.LogError($"Failed exporting ruleset {rulesetId} to JSON file");

                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed exporting ruleset {rulesetId} to JSON file: {ex}");

                    return false;
                }
            }
        }

        public async Task<Ruleset> ImportNewRulesetFromJsonFile(string fileName, bool returnCollections = false)
        {
            try
            {
                var jsonRuleset = await RulesetFileHandler.ReadFromJsonFile(fileName);

                if (jsonRuleset == null)
                {
                    _logger.LogWarning($"Failed importing ruleset from file {fileName}: failed reading JSON file");

                    return null;
                }

                //var ruleset = ConvertToDbModel(jsonRuleset);

                var ruleset = await CreateRulesetAsync(ConvertToDbModel(jsonRuleset));

                if (ruleset == null)
                {
                    _logger.LogWarning($"Failed importing ruleset from file {fileName}: failed creating new ruleset entity");

                    return null;
                }

                var TaskList = new List<Task>();

                if (jsonRuleset.RulesetActionRules.Any())
                {
                    ruleset.RulesetActionRules = jsonRuleset.RulesetActionRules.Select(r => ConvertToDbModel(ruleset.Id, r)).ToList();
                    var actionRulesTask = SaveRulesetActionRules(ruleset.Id, ruleset.RulesetActionRules);
                    TaskList.Add(actionRulesTask);
                }
                
                if (jsonRuleset.RulesetItemCategoryRules.Any())
                {
                    ruleset.RulesetItemCategoryRules = jsonRuleset.RulesetItemCategoryRules.Select(r => ConvertToDbModel(ruleset.Id, r)).ToList();
                    var itemCategoryRulesTask = SaveRulesetItemCategoryRules(ruleset.Id, ruleset.RulesetItemCategoryRules);
                    TaskList.Add(itemCategoryRulesTask);
                }
                
                if (jsonRuleset.RulesetFacilityRules.Any())
                {
                    ruleset.RulesetFacilityRules = jsonRuleset.RulesetFacilityRules.Select(r => ConvertToDbModel(ruleset.Id, r)).ToList();
                    var facilityRulesTask = SaveRulesetFacilityRules(ruleset.Id, ruleset.RulesetFacilityRules);
                    TaskList.Add(facilityRulesTask);
                }

                if (TaskList.Any())
                {
                    await Task.WhenAll(TaskList);
                }

                _logger.LogWarning($"Created ruleset {ruleset.Id} from file {fileName}");

                if (returnCollections)
                {
                    return ruleset;
                }
                else
                {
                    ruleset.RulesetActionRules.Clear();
                    ruleset.RulesetItemCategoryRules.Clear();
                    ruleset.RulesetFacilityRules.Clear();

                    return ruleset;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to import new ruleset from file {fileName}: {ex}");

                return null;
            }
        }

        public IEnumerable<string> GetJsonRulesetFileNames()
        {
            return RulesetFileHandler.GetJsonRulesetFileNames();
        }

        private string GetRulesetFileName(int rulesetId, string rulesetName)
        {
            char[] characters = $"rs{rulesetId}_{rulesetName}".ToCharArray();

            characters = Array.FindAll(characters, (c => (char.IsLetterOrDigit(c)
                                                                || char.IsWhiteSpace(c)
                                                                || c == '-'
                                                                || c == '_')));
            return new string(characters);
        }

        private Ruleset ConvertToDbModel(JsonRuleset jsonRuleset)
        {
            return new Ruleset
            {
                //Id = jsonRuleset.Id,
                Name = jsonRuleset.Name,
                DateCreated = jsonRuleset.DateCreated,
                DateLastModified = jsonRuleset.DateLastModified,
                IsDefault = jsonRuleset.IsDefault,
                IsCustomDefault = false,
                DefaultMatchTitle = jsonRuleset.DefaultMatchTitle,
                DefaultRoundLength = jsonRuleset.DefaultRoundLength
            };
        }

        private RulesetActionRule ConvertToDbModel(int rulesetId, JsonRulesetActionRule jsonRule)
        {
            return BuildRulesetActionRule(rulesetId, jsonRule.ScrimActionType, jsonRule.Points, jsonRule.DeferToItemCategoryRules);
        }

        private RulesetItemCategoryRule ConvertToDbModel(int rulesetId, JsonRulesetItemCategoryRule jsonRule)
        {
            return BuildRulesetItemCategoryRule(rulesetId, jsonRule.ItemCategoryId, jsonRule.Points);
        }

        private RulesetFacilityRule ConvertToDbModel(int rulesetID, JsonRulesetFacilityRule jsonRule)
        {
            return BuildRulesetFacilityRule(rulesetID, jsonRule.FacilityId, jsonRule.MapRegionId);
        }

        #endregion Import / Export JSON

        public static bool IsValidRulesetName(string name)
        {
            return RulesetNameRegex.Match(name).Success;
        }

        public static bool IsValidRulesetDefaultRoundLength(int seconds)
        {
            return seconds > 0;
        }

        public static bool IsValidRulesetDefaultMatchTitle(string title)
        {
            return RulesetDefaultMatchTitleRegex.Match(title).Success || string.IsNullOrWhiteSpace(title);
        }
    }
}
