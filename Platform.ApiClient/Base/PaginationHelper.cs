using Newtonsoft.Json;
using Platform.ApiClient.Models;

namespace Platform.ApiClient.Base;

public static class PaginationHelper
{
    public class Pagination
    {
        public int TotalCount { get; set; }
        public int PageSize { get; set; }
        public int CurrentPage { get; set; }
        public int TotalPages { get; set; }
    }
    public static Pagination GetPagination<T>(this ApiResponse<ICollection<T>> re)
    {
        var hd = re?.Headers?.Where(x => x.Key.ToLower().Equals("x-pagination")).Select(x=>x.Value?.FirstOrDefault()).FirstOrDefault();
        Pagination pg = null;
        if (!string.IsNullOrEmpty(hd))
        {
            try
            {
                pg=  JsonConvert.DeserializeObject<Pagination>(hd);
            }
            catch (Exception )
            {
                //throw new ApiException(500, e.Message);
            } 
        }

        return new Pagination
        {
            TotalCount = pg?.TotalCount ?? re?.Result?.Count ?? 0,
            PageSize = pg?.PageSize ?? re?.Result?.Count ?? 0,
            CurrentPage = pg?.CurrentPage ?? 1,
            TotalPages = pg?.TotalPages ?? 1
        };
    }

    
}