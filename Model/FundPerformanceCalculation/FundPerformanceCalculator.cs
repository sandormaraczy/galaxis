﻿using Microsoft.EntityFrameworkCore;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using GalaxisProjectWebAPI.Infrastructure;
using GalaxisProjectWebAPI.DataModel;
using GalaxisProjectWebAPI.Model.Token;

using DataModelFund = GalaxisProjectWebAPI.DataModel.Fund;
using DataModelFundToken = GalaxisProjectWebAPI.DataModel.FundToken;

namespace GalaxisProjectWebAPI.Model.FundPerformanceCalculation
{
    public class FundPerformanceCalculator : IFundPerformanceCalculator
    {
        private readonly int timeRange = 3600;
        private readonly GalaxisDbContext galaxisContext;

        public FundPerformanceCalculator(GalaxisDbContext galaxisContext)
        {
            this.galaxisContext = galaxisContext;
        }

        public async Task<FundPerformance> CalculateFundPerformance(string fundAddress)
        {
            var fund = await GetFundAsync(fundAddress);
            //uint currentTimeStamp = (uint)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
            uint hardCodedTimeStamp = 1601316060;
            uint diff = hardCodedTimeStamp - fund.DepositStartTimeStamp;

            int resultCount = (int)(diff / timeRange);
            var resultList = new List<long>();
            for (int i = 0; i < resultCount; i++)
            {
                resultList.Add(fund.DepositStartTimeStamp + (i * timeRange));
            }

            var timeStampResults = resultList.Select(x => (uint)x).ToList();

            List<DataModelFundToken> joinedFundTokens = await this.galaxisContext
                .FundTokens
                .Include(item => item.Token)
                .Where(x => x.FundId == fund.Id)
                .OrderByDescending(x => x.Timestamp)
                .ToListAsync();

            var fundTokenMapping = MapFundTokensToRelevantTimeStamp(
                timeStampResults,
                joinedFundTokens);

            TokenPriceHistoricData[] priceHistory = await this.galaxisContext.TokenPriceHistoricDatas
                .Include(x => x.Token)
                .ToArrayAsync();

            var relevantPriceHistory = ApplyHackOnTimeStamps(fundTokenMapping, priceHistory);

            var groupedPriceHistory = relevantPriceHistory.GroupBy(
                x => new { x.Timestamp },
                x => new { x.Token.Symbol, x.UsdPrice },
                (key, result) => new
                {
                    Key = key,
                    Result = result
                }).ToList();

            var finalResult = fundTokenMapping.Join(groupedPriceHistory,
                x => x.Key,
                y => y.Key.Timestamp,
                (x, y) => new { AllocationDetails = x, PriceDetails = y })
                .ToList();

            var resultDictionary = new Dictionary<uint, double>();
            foreach (var resultElement in finalResult)
            {
                var currentAllocation = resultElement.AllocationDetails;
                var currentPriceDetails = resultElement.PriceDetails;

                double currResultValue = 0;
                foreach (var item in currentAllocation.Value)
                {
                    var matchingPriceDetail = currentPriceDetails
                        .Result
                        .FirstOrDefault(x => x.Symbol == item.TokenSymbol);

                    if (matchingPriceDetail != null)
                    {
                        //if (item.TokenSymbol == "CDAI")
                        //{
                        //    currResultValue += (item.Quantity * matchingPriceDetail.UsdPrice)
                        //        + (item.Quantity * DAI Price)
                        //}

                        currResultValue += item.Quantity * matchingPriceDetail.UsdPrice;
                    }
                }

                uint currResultTimeStamp = resultElement.PriceDetails.Key.Timestamp;
                resultDictionary.Add(currResultTimeStamp, Math.Round(currResultValue, 2));
            }

            //    var quantityInfo = currentAllocation.TokenSymbolAndQuantity;
            //    var cucc = currentPriceDetails.Result;
            var performance = new FundPerformance();
            performance.FundValuesByTimeStamps = resultDictionary;

            return performance;
        }

        private TokenPriceHistoricData[] ApplyHackOnTimeStamps(Dictionary<uint, List<TokenAllocationInfo>> fundTokenMapping, TokenPriceHistoricData[] priceHistory)
        {
            var etherResult = priceHistory.Where(x => x.Token.Symbol == "ETH").OrderBy(x => x.Timestamp).ToArray();
            var wethResult = priceHistory.Where(x => x.Token.Symbol == "WETH").OrderBy(x => x.Timestamp).ToArray();
            var daiResult = priceHistory.Where(x => x.Token.Symbol == "DAI").OrderBy(x => x.Timestamp).ToArray();
            var cdaiResult = priceHistory.Where(x => x.Token.Symbol == "CDAI").OrderBy(x => x.Timestamp).ToArray();
            HackAllTypesOfHistoricData(etherResult, fundTokenMapping.Keys.ToList());

            return priceHistory;
        }

        private TokenPriceHistoricData[] HackAllTypesOfHistoricData(TokenPriceHistoricData[] priceHistory, List<uint> keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                uint relevantKey = keys[i];
                priceHistory[i].Timestamp = relevantKey;
            }

            return priceHistory;
        }

        private Dictionary<uint, List<TokenAllocationInfo>> MapFundTokensToRelevantTimeStamp(List<uint> timeStampResults, List<DataModelFundToken> joinedFundTokens)
        {
            var groupedFundTokens = joinedFundTokens.GroupBy(
                            x => new { x.Timestamp },
                            x => new { x.Token.Symbol, x.Quantity },
                            (key, result) => new { Key = key, TokenSymbolAndQuantity = result })
                            .OrderByDescending(x => x.Key.Timestamp)
                            .ToList();

            Dictionary<uint, List<TokenAllocationInfo>> fundTokenMapping = new Dictionary<uint, List<TokenAllocationInfo>>();

            // timestamp matching between result timestamps and grouped fund tokens by timestamps
            uint[] groupedFundTokenTimeStamps = groupedFundTokens.Select(token => token.Key.Timestamp).ToArray();
            List<Tuple<uint, int>> diffs = CalculateTimeStampDiffs(timeStampResults, groupedFundTokenTimeStamps);

            int fundTokenCount = groupedFundTokens.Count;
            for (int i = 0; i < timeStampResults.Count; i++)
            {
                List<TokenAllocationInfo> allocationInfos = new List<TokenAllocationInfo>();
                int relevantIndex = diffs.First(tuple => tuple.Item1 == timeStampResults[i]).Item2;

                foreach (var info in groupedFundTokens[relevantIndex].TokenSymbolAndQuantity)
                {
                    allocationInfos.Add(new TokenAllocationInfo
                    {
                        TokenSymbol = info.Symbol,
                        Quantity = info.Quantity
                    });
                }

                fundTokenMapping.Add(timeStampResults[i], allocationInfos);
            }

            return fundTokenMapping;
        }

        private List<Tuple<uint, int>> CalculateTimeStampDiffs(List<uint> timeStampResults, uint[] groupedFundTokenTimeStamps)
        {
            int fundTokenLength = groupedFundTokenTimeStamps.Length;

            List<Tuple<uint, int>> fundTokenIndexByTimeStamps = new List<Tuple<uint, int>>();

            for (int i = 0; i < timeStampResults.Count; i++)
            {
                uint currTimeStamp = timeStampResults[i];
                List<Tuple<int, int>> diffsByIndexes = new List<Tuple<int, int>>();
                for (int j = 0; j < fundTokenLength; j++)
                {
                    // diffs for timestamps
                    diffsByIndexes.Add(
                        Tuple.Create((int)Math.Abs(currTimeStamp - groupedFundTokenTimeStamps[j]), j));
                }

                int minDiff = diffsByIndexes.Min(tuple => tuple.Item1);

                //overkill, needs optimization
                int resultIndex = diffsByIndexes.First(x => x.Item1 == minDiff).Item2;
                fundTokenIndexByTimeStamps.Add(Tuple.Create(currTimeStamp, resultIndex));
            }

            return fundTokenIndexByTimeStamps;
        }

        private async Task<DataModelFund> GetFundAsync(string fundAddress)
        {
            return await this.galaxisContext
                .Funds
                .FirstOrDefaultAsync(fund => fund.FundAddress == fundAddress);
        }
    }
}