namespace Nop.Services.Caching.Extension
{
    public interface IS3CachingHelper
    {
        bool WriteCache(string content, string fileName, int expireInMinutes = 0);
        string GetCache(string fileName);
        void RemoveCache(string fileName);

        bool TryGetCacheArray(int identifier, string arrayPath, out string content, bool deleteIfExpiresAlready = false);
        bool TryWriteCacheArray(int identifier, string arrayPath, string content, int expireInMinutes = 0);
        bool TryRemoveCacheArray(int identifier, string arrayPath);
    }
}