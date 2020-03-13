﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using squittal.ScrimPlanetmans.CensusServices;
using squittal.ScrimPlanetmans.CensusServices.Models;
using squittal.ScrimPlanetmans.Data;
using squittal.ScrimPlanetmans.Shared.Models.Planetside;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace squittal.ScrimPlanetmans.Services.Planetside
{
    public class FacilityService : IFacilityService
    {
        private readonly IDbContextHelper _dbContextHelper;
        private readonly CensusFacility _censusFacility;
        private readonly ILogger<FacilityService> _logger;

        private List<MapRegion> _scrimmableMapRegions = new List<MapRegion>();

        public FacilityService(IDbContextHelper dbContextHelper, CensusFacility censusFacility, ILogger<FacilityService> logger)
        {
            _dbContextHelper = dbContextHelper;
            _censusFacility = censusFacility;
            _logger = logger;
        }

        public Task<MapRegion> GetMapRegionAsync(int mapRegionId)
        {
            throw new System.NotImplementedException();
        }

        public Task<MapRegion> GetMapRegionFromFacilityIdAsync(int facilityId)
        {
            throw new System.NotImplementedException();
        }

        public Task<MapRegion> GetMapRegionFromFacilityNameAsync(string facilityName)
        {
            throw new System.NotImplementedException();
        }

        public Task<MapRegion> GetMapRegionsByFacilityTypeAsync(int facilityTypeId)
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<MapRegion> GetScrimmableMapRegions()
        {
            return _scrimmableMapRegions;
        }

        public MapRegion GetScrimmableMapRegionFromFacilityId(int facilityId)
        {
            return _scrimmableMapRegions.FirstOrDefault(r => r.FacilityId == facilityId);
        }

        public async Task SetUpScimmableMapRegionsAsync()
        {
            var realZones = new List<int> { 2, 4, 6, 8 };
            var scrimFacilityTypes = new List<int> { 5, 6}; // Small Outpost, Large Outpost

            using var factory = _dbContextHelper.GetFactory();
            var dbContext = factory.GetDbContext();

            _scrimmableMapRegions = await dbContext.MapRegions
                                                    .Where(region => realZones.Contains(region.ZoneId)
                                                                        && scrimFacilityTypes.Contains(region.FacilityTypeId))
                                                    .ToListAsync();
        }

        public async Task RefreshStore()
        {
            bool refreshStore = true;
            bool anyMapRegions = false;
            bool anyFacilityTypes = false;

            using (var factory = _dbContextHelper.GetFactory())
            {
                var dbContext = factory.GetDbContext();

                anyMapRegions = await dbContext.MapRegions.AnyAsync();

                if (anyMapRegions == true)
                {
                    anyFacilityTypes = await dbContext.FacilityTypes.AnyAsync();
                }

                refreshStore = (anyMapRegions == false || anyFacilityTypes == false);
            }

            if (refreshStore != true)
            {
                return;
            }

            var facilityTypes = await _censusFacility.GetAllFacilityTypes();

            if (facilityTypes != null)
            {
                await UpsertRangeAsync(facilityTypes.Select(ConvertToDbModel));

                _logger.LogInformation($"Refreshed Facility Types store: {facilityTypes.Count()} entries");
            }

            var mapRegions = await _censusFacility.GetAllMapRegions();

            if (mapRegions != null)
            {
                await UpsertRangeAsync(mapRegions.Select(ConvertToDbModel));

                _logger.LogInformation($"Refreshed Map Regions store: {mapRegions.Count()} entries");
            }
        }

        private async Task UpsertRangeAsync(IEnumerable<FacilityType> censusEntities)
        {
            var createdEntities = new List<FacilityType>();

            using (var factory = _dbContextHelper.GetFactory())
            {
                var dbContext = factory.GetDbContext();

                var storedEntities = await dbContext.FacilityTypes.ToListAsync();

                foreach (var censusEntity in censusEntities)
                {
                    var storeEntity = storedEntities.FirstOrDefault(e => e.Id == censusEntity.Id);
                    if (storeEntity == null)
                    {
                        createdEntities.Add(censusEntity);
                    }
                    else
                    {
                        storeEntity = censusEntity;
                        dbContext.FacilityTypes.Update(storeEntity);
                    }
                }

                if (createdEntities.Any())
                {
                    await dbContext.FacilityTypes.AddRangeAsync(createdEntities);
                }

                await dbContext.SaveChangesAsync();
            }
        }

        private async Task UpsertRangeAsync(IEnumerable<MapRegion> censusEntities)
        {
            var createdEntities = new List<MapRegion>();

            using (var factory = _dbContextHelper.GetFactory())
            {
                var dbContext = factory.GetDbContext();

                var storedEntities = await dbContext.MapRegions.ToListAsync();

                foreach (var censusEntity in censusEntities)
                {
                    var storeEntity = storedEntities.FirstOrDefault(e => e.Id == censusEntity.Id);
                    if (storeEntity == null)
                    {
                        createdEntities.Add(censusEntity);
                    }
                    else
                    {
                        storeEntity = censusEntity;
                        dbContext.MapRegions.Update(storeEntity);
                    }
                }

                if (createdEntities.Any())
                {
                    await dbContext.MapRegions.AddRangeAsync(createdEntities);
                }

                await dbContext.SaveChangesAsync();
            }
        }

        private FacilityType ConvertToDbModel(CensusFacilityTypeModel censusModel)
        {
            return new FacilityType
            {
                Id = censusModel.FacilityTypeId,
                Description = censusModel.Description
            };
        }

        private MapRegion ConvertToDbModel(CensusMapRegionModel censusModel)
        {
            return new MapRegion
            {
                Id = censusModel.MapRegionId,
                FacilityId = censusModel.FacilityId,
                FacilityName = censusModel.FacilityName,
                FacilityTypeId = censusModel.FacilityTypeId,
                FacilityType = censusModel.FacilityType,
                ZoneId = censusModel.ZoneId
            };
        }
    }
}