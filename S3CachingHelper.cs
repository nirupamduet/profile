using System;
using System.Text;
using Nop.Core;
using Nop.Core.Domain.Media;
using Nop.Services.Aws;

namespace Nop.Services.Caching.Extension
{
    public class S3CachingHelper : IS3CachingHelper
    {
        #region Field

        private readonly IAwsS3Helper _s3Helper;
        private readonly MediaSettings _mediaSettings;

        #endregion

        #region Ctr

        public S3CachingHelper(IAwsS3Helper s3Helper, MediaSettings mediaSettings)
        {
            _s3Helper = s3Helper;
            _mediaSettings = mediaSettings;
        }

        #endregion

        #region Cache File (Single)

        /// <summary>
        /// Write cache
        /// </summary>
        /// <param name="content"></param>
        /// <param name="fileName"></param>
        /// <param name="expireInMinutes"></param>
        /// <returns></returns>
        public bool WriteCache(string content, string fileName, int expireInMinutes = 0)
        {
            //cache to aws S3
            if (_mediaSettings.AWSS3Enable)
            {
                _s3Helper.S3UploadFile(fileName, Encoding.UTF8.GetBytes(content), "txt", expireInMinutes);
            }
            //cache to wwwroot
            else
            {
                var filePath = CommonHelper.MapPath($"~/caches/{fileName}.txt");
                System.IO.File.WriteAllText(filePath, String.Empty);
                System.IO.File.WriteAllText(filePath, content);
            }

            return true;
        }

        /// <summary>
        /// Get S3 cache content by name
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public string GetCache(string fileName)
        {
            //get cache data form S3
            if (_mediaSettings.AWSS3Enable)
            {
                if (_s3Helper.S3ExistsFile(fileName))
                    return _s3Helper.S3GetFile(fileName);
                
                //empty
                return string.Empty;
            }

            //cache from file(wwwroot)
            var filePath = CommonHelper.MapPath($"~/caches/{fileName}.txt");

            if (System.IO.File.Exists(filePath))
                return System.IO.File.ReadAllText(filePath);

            //empty
            return string.Empty;
        }

        /// <summary>
        /// Remove Cache
        /// </summary>
        /// <param name="fileName"></param>
        public void RemoveCache(string fileName)
        {
            if (_mediaSettings.AWSS3Enable)
            {
                _s3Helper.S3DeleteFile(fileName);
            }
            else
            {
                //cache form file(wwwroot)
                var filePath = CommonHelper.MapPath($"~/caches/{fileName}.txt");

                //delete if exists
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
        }

        #endregion

        #region File Cache Array

        /// <summary>
        /// Get S3 cache file content (delete if expires based on S3 object expire header
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="arrayPath"></param>
        /// <param name="content"></param>
        /// <param name="deleteIfExpiresAlready"></param>
        /// <returns></returns>
        public bool TryGetCacheArray(int identifier, string arrayPath, out string content, bool deleteIfExpiresAlready = false)
        {
            var fileName = $"{arrayPath}/{identifier}";

            content = string.Empty;

            //get cache data form S3
            if (_mediaSettings.AWSS3Enable)
            {
                if (_s3Helper.S3ExistsFile(fileName))
                {
                    content = _s3Helper.S3GetFile(fileName, deleteIfExpiresAlready);
                    return true;
                }
            }
            else
            {
                //cache form file(wwwroot)
                var filePath = CommonHelper.MapPath($"~/caches/{fileName}.txt");

                if (System.IO.File.Exists(filePath))
                {
                    content = System.IO.File.ReadAllText(filePath);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Write cache
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="arrayPath"></param>
        /// <param name="content"></param>
        /// <param name="expireInMinutes"></param>
        /// <returns></returns>
        public bool TryWriteCacheArray(int identifier, string arrayPath, string content, int expireInMinutes = 0)
        {
            var fileName = $"{arrayPath}/{identifier}";

            //cache to aws S3
            if (_mediaSettings.AWSS3Enable)
            {
                _s3Helper.S3UploadFile(fileName, Encoding.UTF8.GetBytes(content), "txt", expireInMinutes);
            }
            //cache to wwwroot
            else
            {
                var filePath = CommonHelper.MapPath($"~/caches/{fileName}.txt");
                System.IO.File.WriteAllText(filePath, String.Empty);
                System.IO.File.WriteAllText(filePath, content);
            }

            return true;
        }

        /// <summary>
        /// Remove S3 Cache file array
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="arrayPath"></param>
        public bool TryRemoveCacheArray(int identifier, string arrayPath)
        {
            var fileName = $"{arrayPath}/{identifier}";

            if (_mediaSettings.AWSS3Enable)
            {
                if (_s3Helper.S3ExistsFile(fileName))
                {
                    _s3Helper.S3DeleteFile(fileName);
                    return true;
                }
            }
            else
            {
                //cache form file(wwwroot)
                var filePath = CommonHelper.MapPath($"~/caches/{fileName}.txt");

                //delete if exists
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                    return true;
                }
            }

            return true;
        }

        #endregion
    }
}