using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService
{
    /// <summary>
    /// 投幣
    /// </summary>
    public class DonateCoinDomainService : IDonateCoinDomainService
    {
        private readonly ILogger<DonateCoinDomainService> _logger;
        private readonly BiliCookie _biliBiliCookie;
        private readonly DailyTaskOptions _dailyTaskOptions;
        private readonly IAccountApi _accountApi;
        private readonly ICoinDomainService _coinDomainService;
        private readonly IVideoDomainService _videoDomainService;
        private readonly IRelationApi _relationApi;
        private readonly IVideoApi _videoApi;
        private readonly Dictionary<string, int> _expDic;
        private readonly Dictionary<string, string> _donateContinueStatusDic;

        /// <summary>
        /// up的視頻稿件總數緩存
        /// </summary>
        private readonly Dictionary<long, int> _upVideoCountDicCatch = new();

        /// <summary>
        /// 已對視頻投幣數量緩存
        /// </summary>
        private readonly Dictionary<string, int> _alreadyDonatedCoinCountCatch = new();

        public DonateCoinDomainService(
            ILogger<DonateCoinDomainService> logger,
            BiliCookie cookie,
            IOptionsMonitor<DailyTaskOptions> dailyTaskOptions,
            IAccountApi accountApi,
            ICoinDomainService coinDomainService,
            IVideoDomainService videoDomainService,
            IRelationApi relationApi,
            IOptionsMonitor<Dictionary<string, int>> expDicOptions,
            IOptionsMonitor<Dictionary<string, string>> donateContinueStatusDicOptions,
            IVideoApi videoApi
            )
        {
            _logger = logger;
            _biliBiliCookie = cookie;
            _dailyTaskOptions = dailyTaskOptions.CurrentValue;
            _accountApi = accountApi;
            _coinDomainService = coinDomainService;
            _videoDomainService = videoDomainService;
            _relationApi = relationApi;
            _videoApi = videoApi;
            _expDic = expDicOptions.Get(Constants.OptionsNames.ExpDictionaryName);
            _donateContinueStatusDic = donateContinueStatusDicOptions.Get(Constants.OptionsNames.DonateCoinCanContinueStatusDictionaryName);
        }

        /// <summary>
        /// 完成投幣任務
        /// </summary>
        public async Task AddCoinsForVideos()
        {
            int needCoins = await GetNeedDonateCoinNum();
            int protectedCoins = _dailyTaskOptions.NumberOfProtectedCoins;
            if (needCoins <= 0) return;

            //投幣前硬幣余額
            decimal coinBalance = await _coinDomainService.GetCoinBalance();
            _logger.LogInformation("【投幣前余額】 : {coinBalance}", coinBalance);
            _ = int.TryParse(decimal.Truncate(coinBalance - protectedCoins).ToString(), out int unprotectedCoins);

            if (coinBalance <= 0)
            {
                _logger.LogInformation("因硬幣余額不足，今日暫不執行投幣任務");
                return;
            }

            if (coinBalance <= protectedCoins)
            {
                _logger.LogInformation("因硬幣余額達到或低於保留值，今日暫不執行投幣任務");
                return;
            }

            //余額小於目標投幣數，按余額投
            if (coinBalance < needCoins)
            {
                _ = int.TryParse(decimal.Truncate(coinBalance).ToString(), out needCoins);
                _logger.LogInformation("因硬幣余額不足，目標投幣數調整為: {needCoins}", needCoins);
            }

            //投幣後余額小於等於保護值，按保護值允許投
            if (coinBalance - needCoins <= protectedCoins)
            {
                //排除需投等於保護後可投數量相等時的情況
                if (unprotectedCoins != needCoins)
                {
                    needCoins = unprotectedCoins;
                    _logger.LogInformation("因硬幣余額投幣後將達到或低於保留值，目標投幣數調整為: {needCoins}", needCoins);
                }
            }

            int success = 0;
            int tryCount = 10;
            for (int i = 1; i <= tryCount && success < needCoins; i++)
            {
                _logger.LogDebug("開始嘗試第{num}次", i);

                UpVideoInfo video = await TryGetCanDonatedVideo();
                if (video == null) continue;

                _logger.LogInformation("【視頻】{title}", video.Title);

                bool re = await DoAddCoinForVideo(video, _dailyTaskOptions.SelectLike);
                if (re) success++;
            }

            if (success == needCoins)
                _logger.LogInformation("視頻投幣任務完成");
            else
                _logger.LogInformation("投幣嘗試超過10次，已終止");

            _logger.LogInformation("【硬幣余額】{coin}", (await _accountApi.GetCoinBalance()).Data.Money ?? 0);
        }

        /// <summary>
        /// 嘗試獲取一個可以投幣的視頻
        /// </summary>
        /// <returns></returns>
        public async Task<UpVideoInfo> TryGetCanDonatedVideo()
        {
            UpVideoInfo result;

            //從配置的up中隨機嘗試獲取1次
            result = await TryGetCanDonateVideoByConfigUps(1);
            if (result != null) return result;

            //然後從特別關注列表嘗試獲取1次
            result = await TryGetCanDonateVideoBySpecialUps(1);
            if (result != null) return result;

            //然後從普通關注列表獲取1次
            result = await TryGetCanDonateVideoByFollowingUps(1);
            if (result != null) return result;

            //最後從排行榜嘗試5次
            result = await TryGetCanDonateVideoByRegion(5);

            return result;
        }

        /// <summary>
        /// 為視頻投幣
        /// </summary>
        /// <param name="aid">av號</param>
        /// <param name="multiply">投幣數量</param>
        /// <param name="select_like">是否同時點讚 1是0否</param>
        /// <returns>是否投幣成功</returns>
        public async Task<bool> DoAddCoinForVideo(UpVideoInfo video, bool select_like)
        {
            BiliApiResponse result;
            try
            {
                var request = new AddCoinRequest(video.Aid, _biliBiliCookie.BiliJct)
                {
                    Select_like = select_like ? 1 : 0
                };
                var referer = $"https://www.bilibili.com/video/{video.Bvid}/?spm_id_from=333.1007.tianma.1-1-1.click&vd_source=80c1601a7003934e7a90709c18dfcffd";
                result = await _videoApi.AddCoinForVideo(request, referer);
            }
            catch (Exception)
            {
                return false;
            }

            if (result.Code == 0)
            {
                _expDic.TryGetValue("每日投幣", out int exp);
                _logger.LogInformation("投幣成功，經驗+{exp} √", exp);
                return true;
            }

            if (_donateContinueStatusDic.Any(x => x.Key == result.Code.ToString()))
            {
                _logger.LogError("投幣失敗，原因：{msg}", result.Message);
                return false;
            }

            else
            {
                string errorMsg = $"投幣發生未預計異常：{result.Message}";
                _logger.LogError(errorMsg);
                throw new Exception(errorMsg);
            }
        }

        #region private

        /// <summary>
        /// 獲取今日的目標投幣數
        /// </summary>
        /// <returns></returns>
        private async Task<int> GetNeedDonateCoinNum()
        {
            //獲取自定義配置投幣數
            int configCoins = _dailyTaskOptions.NumberOfCoins;

            if (configCoins <= 0)
            {
                _logger.LogInformation("已配置為跳過投幣任務");
                return configCoins;
            }

            //已投的硬幣
            int alreadyCoins = await _coinDomainService.GetDonatedCoins();
            //目標
            //int targetCoins = configCoins > Constants.MaxNumberOfDonateCoins
            //    ? Constants.MaxNumberOfDonateCoins
            //    : configCoins;
            int targetCoins = configCoins;

            _logger.LogInformation("【今日已投】{already}枚", alreadyCoins);
            _logger.LogInformation("【目標欲投】{already}枚", targetCoins);

            if (targetCoins > alreadyCoins)
            {
                int needCoins = targetCoins - alreadyCoins;
                _logger.LogInformation("【還需再投】{need}枚", needCoins);
                return needCoins;
            }

            _logger.LogInformation("已完成投幣任務，不需要再投啦~");
            return 0;
        }

        /// <summary>
        /// 嘗試從配置的up主里隨機獲取一個可以投幣的視頻
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private async Task<UpVideoInfo> TryGetCanDonateVideoByConfigUps(int tryCount)
        {
            //是否配置了up主
            if (_dailyTaskOptions.SupportUpIdList.Count == 0) return null;

            return await TryCanDonateVideoByUps(_dailyTaskOptions.SupportUpIdList, tryCount); ;
        }

        /// <summary>
        /// 嘗試從特別關注的Up主中隨機獲取一個可以投幣的視頻
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private async Task<UpVideoInfo> TryGetCanDonateVideoBySpecialUps(int tryCount)
        {
            //獲取特別關注列表
            var request = new GetSpecialFollowingsRequest(long.Parse(_biliBiliCookie.UserId));
            BiliApiResponse<List<UpInfo>> specials = await _relationApi.GetFollowingsByTag(request);
            if (specials.Data == null || specials.Data.Count == 0) return null;

            return await TryCanDonateVideoByUps(specials.Data.Select(x => x.Mid).ToList(), tryCount);
        }

        /// <summary>
        /// 嘗試從普通關注的Up主中隨機獲取一個可以投幣的視頻
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private async Task<UpVideoInfo> TryGetCanDonateVideoByFollowingUps(int tryCount)
        {
            //獲取特別關注列表
            var request = new GetFollowingsRequest(long.Parse(_biliBiliCookie.UserId));
            BiliApiResponse<GetFollowingsResponse> result = await _relationApi.GetFollowings(request);
            if (result.Data.Total == 0) return null;

            return await TryCanDonateVideoByUps(result.Data.List.Select(x => x.Mid).ToList(), tryCount);
        }

        /// <summary>
        /// 嘗試從排行榜中獲取一個沒有看過的視頻
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private async Task<UpVideoInfo> TryGetCanDonateVideoByRegion(int tryCount)
        {
            try
            {
                for (int i = 0; i < tryCount; i++)
                {
                    RankingInfo video = await _videoDomainService.GetRandomVideoOfRanking();
                    if (!await IsCanDonate(video.Aid.ToString())) continue;
                    return new UpVideoInfo()
                    {
                        Aid = video.Aid,
                        Bvid = video.Bvid,
                        Title = video.Title
                    };
                }
            }
            catch (Exception e)
            {
                //ignore
                _logger.LogWarning("異常：{msg}", e);
            }
            return null;
        }

        /// <summary>
        /// 嘗試從指定的up主集合中隨機獲取一個可以嘗試投幣的視頻
        /// </summary>
        /// <param name="upIds"></param>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private async Task<UpVideoInfo> TryCanDonateVideoByUps(List<long> upIds, int tryCount)
        {
            if (upIds == null || upIds.Count == 0) return null;

            try
            {
                //嘗試tryCount次
                for (int i = 1; i <= tryCount; i++)
                {
                    //獲取隨機Up主Id
                    long randomUpId = upIds[new Random().Next(0, upIds.Count)];

                    if (randomUpId == 0 || randomUpId == long.MinValue) continue;

                    if (randomUpId.ToString() == _biliBiliCookie.UserId)
                    {
                        _logger.LogDebug("不能為自己投幣");
                        continue;
                    }

                    //該up的視頻總數
                    if (!_upVideoCountDicCatch.TryGetValue(randomUpId, out int videoCount))
                    {
                        videoCount = await _videoDomainService.GetVideoCountOfUp(randomUpId);
                        _upVideoCountDicCatch.Add(randomUpId, videoCount);
                    }
                    if (videoCount == 0) continue;

                    UpVideoInfo videoInfo = await _videoDomainService.GetRandomVideoOfUp(randomUpId, videoCount);
                    _logger.LogDebug("獲取到視頻{aid}({title})", videoInfo.Aid, videoInfo.Title);

                    //檢查是否可以投
                    if (!await IsCanDonate(videoInfo.Aid.ToString())) continue;

                    return videoInfo;
                }
            }
            catch (Exception e)
            {
                //ignore
                _logger.LogWarning("異常：{msg}", e);
            }

            return null;
        }

        /// <summary>
        /// 已為視頻投幣個數是否小於最大限制
        /// </summary>
        /// <param name="aid">av號</param>
        /// <returns></returns>
        private async Task<bool> IsDonatedLessThenLimitCoinsForVideo(string aid)
        {
            try
            {
                //獲取已投幣數量
                if (!_alreadyDonatedCoinCountCatch.TryGetValue(aid, out int multiply))
                {
                    multiply = (await _videoApi.GetDonatedCoinsForVideo(new GetAlreadyDonatedCoinsRequest(long.Parse(aid))))
                        .Data.Multiply;
                    _alreadyDonatedCoinCountCatch.TryAdd(aid, multiply);
                }

                _logger.LogDebug("已為Av{aid}投過{num}枚硬幣", aid, multiply);

                if (multiply >= 2) return false;

                //獲取該視頻可投幣數量
                int limitCoinNum = (await _videoDomainService.GetVideoDetail(aid)).Copyright == 1
                    ? 2 //原創，最多可投2枚
                    : 1;//轉載，最多可投1枚
                _logger.LogDebug("該視頻的最大投幣數為{num}", limitCoinNum);

                return multiply < limitCoinNum;
            }
            catch (Exception e)
            {
                //ignore
                _logger.LogWarning("異常：{mag}", e);
                return false;
            }
        }

        /// <summary>
        /// 檢查獲取到的視頻是否可以投幣
        /// </summary>
        /// <param name="aid"></param>
        /// <returns></returns>
        private async Task<bool> IsCanDonate(string aid)
        {
            //本次運行已經嘗試投過的,不進行重覆投（不管成功還是失敗，凡取過嘗試過的，不重覆嘗試）
            if (_alreadyDonatedCoinCountCatch.Any(x => x.Key == aid))
            {
                _logger.LogDebug("重覆視頻，丟棄處理");
                return false;
            }

            //已經投滿2個幣的，不能再投
            if (!await IsDonatedLessThenLimitCoinsForVideo(aid))
            {
                _logger.LogDebug("超出單個視頻投幣數量限制，丟棄處理");
                return false;
            }

            return true;
        }

        #endregion
    }
}
