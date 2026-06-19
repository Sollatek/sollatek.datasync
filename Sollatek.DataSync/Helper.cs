using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.ApiClient.Base;
using Platform.ApiClient.Models;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync;

public static class Helper
{
    public static string GetDatabaseConnection(this IConfiguration configuration)
    {
        return configuration.GetRequiredSection("Settings").GetValue<string>("dbConnection", null);
    }

    public static string[] GetSyncPlan(this IConfiguration configuration)
    {
        return SyncPlanConfigurationReader.Read(configuration)
            .Select(x => x.EntityKey)
            .ToArray();
    }

    public static int GetMaxPageSize(this IConfiguration configuration)
    {
        return SyncOptions.FromConfiguration(configuration).MaxPageSize;
    }

    public static Task<bool> DoForAll<T>(
        IServiceProvider serviceProvider,
        Func<int?, int?, Task<ApiResponse<ICollection<T>>>> func,
        Func<ApiResponse<ICollection<T>>, Task> doAction,
        int maxTotalProcess = Int32.MaxValue)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();

        return DoForAll(func, doAction, configuration.GetMaxPageSize(), maxTotalProcess);
    }

    public static async Task<bool> DoForAll<T>(Func<int?, int?, Task<ApiResponse<ICollection<T>>>> func,
        Func<ApiResponse<ICollection<T>>, Task> doAction, int top = 500, int maxTotalProcess = Int32.MaxValue)
    {
        var s = await func.Invoke(top, 0);

        await doAction(s);
        PaginationHelper.Pagination pg = s.GetPagination();
        var count = s.Result.Count;

        if (pg.TotalPages > 1 && count < maxTotalProcess)
        {
            var i = 2;
            do 
            {
                var re = await func.Invoke(top, top * (i - 1));

                await doAction(re);
                var newpg = re.GetPagination();
                if (re.Result.Count == 0)
                {
                    return false;
                }
                
                count += re.Result.Count;
                if (count >= maxTotalProcess)
                {
                    return true;
                }

                if (newpg.TotalPages < pg.TotalPages)
                {
                    return false;
                }

                pg = newpg;
                i += 1;
            }
            while (i <= pg.TotalPages) ;
        }

        return false;
    }
}
