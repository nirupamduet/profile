using Nop.Services.Media;
using System;
using Nop.Core;
using Nop.Core.Data;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Media;
using Nop.Services.Configuration;
using Nop.Services.Events;
using Nop.Services.Logging;
using System.IO;
using System.Drawing;
using Nop.Services.Seo;
using ImageResizer;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon;
using Amazon.Runtime;
using Amazon.S3.IO;
using Nop.Data;
using Microsoft.AspNetCore.Hosting;
using System.Threading;

namespace Nop.Services.Aws
{
    public partial class AwsPictureService : PictureService, IPictureService
    {
        #region Const

        private const int MULTIPLE_THUMB_DIRECTORIES_LENGTH = 3;

        #endregion

        #region Fields

        private readonly IRepository<Picture> _pictureRepository;
        private readonly ISettingService _settingService;
        private readonly IWebHelper _webHelper;
        private readonly ILogger _logger;
        private readonly IEventPublisher _eventPublisher;
        private readonly MediaSettings _mediaSettings;
        private readonly IHostingEnvironment _hostingEnvironment;

        #endregion

        #region Ctr

        public AwsPictureService(IRepository<Picture> pictureRepository,
            IRepository<ProductPicture> productPictureRepository,
            ISettingService settingService,
            IWebHelper webHelper,
            ILogger logger,
            IDbContext dbContext,
            IEventPublisher eventPublisher,
            MediaSettings mediaSettings,
            IDataProvider dataProvider,
            IHostingEnvironment hostingEnvironment) 
            : base(  pictureRepository,
            productPictureRepository,
              settingService,
              webHelper,
              logger,
              dbContext,
              eventPublisher,
              mediaSettings,
              dataProvider,
              hostingEnvironment)
        {
            this._pictureRepository = pictureRepository;
            this._settingService = settingService;
            this._webHelper = webHelper;
            this._logger = logger;
            this._eventPublisher = eventPublisher;
            this._mediaSettings = mediaSettings;
            this._hostingEnvironment = hostingEnvironment;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Calculates picture dimensions whilst maintaining aspect
        /// </summary>
        /// <param name="originalSize">The original picture size</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="resizeType">Resize type</param>
        /// <param name="ensureSizePositive">A value indicating whether we should ensure that size values are positive</param>
        /// <returns></returns>
        protected new virtual Size CalculateDimensions(Size originalSize, int targetSize,
            ResizeType resizeType = ResizeType.LongestSide, bool ensureSizePositive = true)
        {
            var newSize = new Size();
            switch (resizeType)
            {
                case ResizeType.LongestSide:
                    if (originalSize.Height > originalSize.Width)
                    {
                        // portrait 
                        newSize.Width = (int)(originalSize.Width * (float)(targetSize / (float)originalSize.Height));
                        newSize.Height = targetSize;
                    }
                    else
                    {
                        // landscape or square
                        newSize.Height = (int)(originalSize.Height * (float)(targetSize / (float)originalSize.Width));
                        newSize.Width = targetSize;
                    }
                    break;
                case ResizeType.Width:
                    newSize.Height = (int)(originalSize.Height * (float)(targetSize / (float)originalSize.Width));
                    newSize.Width = targetSize;
                    break;
                case ResizeType.Height:
                    newSize.Width = (int)(originalSize.Width * (float)(targetSize / (float)originalSize.Height));
                    newSize.Height = targetSize;
                    break;
                default:
                    throw new Exception("Not supported ResizeType");
            }

            if (ensureSizePositive)
            {
                if (newSize.Width < 1)
                    newSize.Width = 1;
                if (newSize.Height < 1)
                    newSize.Height = 1;
            }

            return newSize;
        }

        /// <summary>
        /// Returns the file extension from mime type.
        /// </summary>
        /// <param name="mimeType">Mime type</param>
        /// <returns>File extension</returns>
        protected new virtual string GetFileExtensionFromMimeType(string mimeType)
        {
            if (mimeType == null)
                return null;

            //also see System.Web.MimeMapping for more mime types

            string[] parts = mimeType.Split('/');
            string lastPart = parts[parts.Length - 1];
            switch (lastPart)
            {
                case "pjpeg":
                    lastPart = "jpg";
                    break;
                case "x-png":
                    lastPart = "png";
                    break;
                case "x-icon":
                    lastPart = "ico";
                    break;
            }
            return lastPart;
        }

        /// <summary>
        /// Loads a picture from file
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <param name="mimeType">MIME type</param>
        /// <returns>Picture binary</returns>
        public new virtual byte[] LoadPictureFromFile(int pictureId, string mimeType)
        {
            if (_mediaSettings.AWSS3Enable)
            {
                string lastPart = GetFileExtensionFromMimeType(mimeType);
                string fileName = string.Format("{0}_0.{1}", pictureId.ToString("0000000"), lastPart);
                var filePath = GetPictureLocalPath(fileName);
                if (!S3ExistsImage(fileName))
                    return new byte[0];

                return S3ReadAllBytes(fileName);
            }
            else
            {
                string lastPart = GetFileExtensionFromMimeType(mimeType);
                string fileName = string.Format("{0}_0.{1}", pictureId.ToString("0000000"), lastPart);
                var filePath = GetPictureLocalPath(fileName);
                if (!File.Exists(filePath))
                    return new byte[0];
                return File.ReadAllBytes(filePath);
            }
        }

        /// <summary>
        /// Save picture on file system
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <param name="pictureBinary">Picture binary</param>
        /// <param name="mimeType">MIME type</param>
        protected new virtual void SavePictureInFile(int pictureId, byte[] pictureBinary, string mimeType)
        {
            //string lastPart = GetFileExtensionFromMimeType(mimeType);
            //string fileName = string.Format("{0}_0.{1}", pictureId.ToString("0000000"), lastPart);

            var lastPart = GetFileExtensionFromMimeType(mimeType);
            var fileName = $"{pictureId:0000000}_0.{lastPart}";

            //AWS S3
            if (_mediaSettings.AWSS3Enable)
                S3UploadImage(fileName, pictureBinary, mimeType);
            else
                File.WriteAllBytes(GetPictureLocalPath(fileName), pictureBinary);

        }

        /// <summary>
        /// Delete a picture on file system
        /// </summary>
        /// <param name="picture">Picture</param>
        protected new virtual void DeletePictureOnFileSystem(Picture picture)
        {
            if (picture == null)
                throw new ArgumentNullException("picture");

            string lastPart = GetFileExtensionFromMimeType(picture.MimeType);
            string fileName = string.Format("{0}_0.{1}", picture.Id.ToString("0000000"), lastPart);
            string filePath = GetPictureLocalPath(fileName);

            if (_mediaSettings.AWSS3Enable)
            {
                S3DeleteImage(filePath, picture.MimeType);
            }
            else
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        /// <summary>
        /// Delete picture thumbs
        /// </summary>
        /// <param name="picture">Picture</param>
        protected new virtual void DeletePictureThumbs(Picture picture)
        {
            if (_mediaSettings.AWSS3Enable)
            {
                // Create a client
                var client = GetS3Client();
                string prefix = string.Format("{0}", picture.Id.ToString("0000000"));
                // List all objects
                var listRequest = new ListObjectsRequest
                {
                    BucketName = _mediaSettings.AWSS3BucketName,
                    Prefix = $"images/thumbs/{prefix}"
                };

                ListObjectsResponse listResponse;
                do
                {
                    // Get a list of objects
                    listResponse = client.ListObjects(listRequest);
                    foreach (S3Object obj in listResponse.S3Objects)
                    {
                        //delete
                        S3DeleteImageFromThumbs(obj.Key, picture.MimeType);
                    }

                    // Set the marker property
                    listRequest.Marker = listResponse.NextMarker;
                } while (listResponse.IsTruncated);
            }
            else
            {
                string filter = string.Format("{0}*.*", picture.Id.ToString("0000000"));
                var thumbDirectoryPath = Path.Combine(_hostingEnvironment.WebRootPath, "images\\thumbs");
                //_webHelper.MapPath("~/content/images/thumbs");
                string[] currentFiles = System.IO.Directory.GetFiles(thumbDirectoryPath, filter, SearchOption.AllDirectories);
                foreach (string currentFileName in currentFiles)
                {
                    var thumbFilePath = GetThumbLocalPath(currentFileName);
                    File.Delete(thumbFilePath);
                }
            }
        }

        /// <summary>
        /// Get picture (thumb) local path
        /// </summary>
        /// <param name="thumbFileName">Filename</param>
        /// <returns>Local picture thumb path</returns>
        protected new virtual string GetThumbLocalPath(string thumbFileName)
        {
            if (_mediaSettings.AWSS3Enable)
            {
                return $"{_mediaSettings.AWSS3RootUrl}/{_mediaSettings.AWSS3BucketName}/images/thumbs/{thumbFileName}";
            }
            else
            {
                var thumbsDirectoryPath = Path.Combine(_hostingEnvironment.WebRootPath, "images\\thumbs"); ;
                if (_mediaSettings.MultipleThumbDirectories)
                {
                    //get the first two letters of the file name
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(thumbFileName);
                    if (fileNameWithoutExtension != null && fileNameWithoutExtension.Length > MULTIPLE_THUMB_DIRECTORIES_LENGTH)
                    {
                        var subDirectoryName = fileNameWithoutExtension.Substring(0, MULTIPLE_THUMB_DIRECTORIES_LENGTH);
                        thumbsDirectoryPath = Path.Combine(thumbsDirectoryPath, subDirectoryName);
                        if (!System.IO.Directory.Exists(thumbsDirectoryPath))
                        {
                            System.IO.Directory.CreateDirectory(thumbsDirectoryPath);
                        }
                    }
                }
                var thumbFilePath = Path.Combine(thumbsDirectoryPath, thumbFileName);
                return thumbFilePath;
            }
        }

        /// <summary>
        /// Get picture (thumb) URL 
        /// </summary>
        /// <param name="thumbFileName">Filename</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <returns>Local picture thumb path</returns>
        protected new virtual string GetThumbUrl(string thumbFileName, string storeLocation = null)
        {
            if (_mediaSettings.AWSS3Enable)
            {
                var url = $"{_mediaSettings.AWSS3RootUrl}/{_mediaSettings.AWSS3BucketName}/images/thumbs/";

                if (_mediaSettings.MultipleThumbDirectories)
                {
                    //get the first two letters of the file name
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(thumbFileName);
                    if (fileNameWithoutExtension != null && fileNameWithoutExtension.Length > MULTIPLE_THUMB_DIRECTORIES_LENGTH)
                    {
                        var subDirectoryName = fileNameWithoutExtension.Substring(0, MULTIPLE_THUMB_DIRECTORIES_LENGTH);
                        url = url + subDirectoryName + "/";
                    }
                }

                url = url + thumbFileName;
                return url;
            }
            else
            {
                storeLocation = !String.IsNullOrEmpty(storeLocation)
                                        ? storeLocation
                                        : _webHelper.GetStoreLocation();
                var url = storeLocation + "/images/thumbs/";

                if (_mediaSettings.MultipleThumbDirectories)
                {
                    //get the first two letters of the file name
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(thumbFileName);
                    if (fileNameWithoutExtension != null && fileNameWithoutExtension.Length > MULTIPLE_THUMB_DIRECTORIES_LENGTH)
                    {
                        var subDirectoryName = fileNameWithoutExtension.Substring(0, MULTIPLE_THUMB_DIRECTORIES_LENGTH);
                        url = url + subDirectoryName + "/";
                    }
                }

                url = url + thumbFileName;
                return url;
            }
        }

        /// <summary>
        /// Get picture local path. Used when images stored on file system (not in the database)
        /// </summary>
        /// <param name="fileName">Filename</param>
        /// <returns>Local picture path</returns>
        protected new virtual string GetPictureLocalPath(string fileName)
        {
            if (_mediaSettings.AWSS3Enable)
            {
                return $"{_mediaSettings.AWSS3RootUrl}/{_mediaSettings.AWSS3BucketName}/images/{fileName}";
            }
            else
            {
                var imagesDirectoryPath = Path.Combine(_hostingEnvironment.WebRootPath, "images\\thumbs");
                var filePath = Path.Combine(imagesDirectoryPath, fileName);
                return filePath;
            }
        }

        /// <summary>
        /// Gets the loaded picture binary depending on picture storage settings
        /// </summary>
        /// <param name="picture">Picture</param>
        /// <param name="fromDb">Load from database; otherwise, from file system</param>
        /// <returns>Picture binary</returns>
        protected new virtual byte[] LoadPictureBinary(Picture picture, bool fromDb)
        {
            if (picture == null)
                throw new ArgumentNullException("picture");

            byte[] result = null;
            if (fromDb)
                result = picture.PictureBinary;
            else
                result = LoadPictureFromFile(picture.Id, picture.MimeType);
            return result;
        }

        #endregion

        #region Getting picture local path/URL methods

        /// <summary>
        /// Gets the loaded picture binary depending on picture storage settings
        /// </summary>
        /// <param name="picture">Picture</param>
        /// <returns>Picture binary</returns>
        public new virtual byte[] LoadPictureBinary(Picture picture)
        {
            return LoadPictureBinary(picture, this.StoreInDb);
        }

        /// <summary>
        /// Get picture SEO friendly name
        /// </summary>
        /// <param name="name">Name</param>
        /// <returns>Result</returns>
        public new virtual string GetPictureSeName(string name)
        {
            return SeoExtensions.GetSeName(name, true, false);
        }

        /// <summary>
        /// Gets the default picture URL
        /// </summary>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="defaultPictureType">Default picture type</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <returns>Picture URL</returns>
        public new virtual string GetDefaultPictureUrl(int targetSize = 0,
            PictureType defaultPictureType = PictureType.Entity,
            string storeLocation = null)
        {
            string defaultImageFileName;
            switch (defaultPictureType)
            {
                case PictureType.Entity:
                    defaultImageFileName = _settingService.GetSettingByKey("Media.DefaultImageName", "default-image.gif");
                    break;
                case PictureType.Avatar:
                    defaultImageFileName = _settingService.GetSettingByKey("Media.Customer.DefaultAvatarImageName", "default-avatar.jpg");
                    break;
                default:
                    defaultImageFileName = _settingService.GetSettingByKey("Media.DefaultImageName", "default-image.gif");
                    break;
            }

            string filePath = GetPictureLocalPath(defaultImageFileName);

            if (_mediaSettings.AWSS3Enable)
            {
                if (!S3ExistsImage(defaultImageFileName))
                {
                    return "";
                }
            }
            else
            {
                if (!File.Exists(filePath))
                {
                    return "";
                }
            }

            if (targetSize == 0)
            {
                if (_mediaSettings.AWSS3Enable)
                {
                    return $"{_mediaSettings.AWSS3RootUrl}/{_mediaSettings.AWSS3BucketName}/images/{defaultImageFileName}";
                }
                else
                {
                    string url = (!String.IsNullOrEmpty(storeLocation)
                                     ? storeLocation
                                     : _webHelper.GetStoreLocation())
                                     + "/images/" + defaultImageFileName;
                    return url;
                }
            }
            else
            {
                if (_mediaSettings.AWSS3Enable)
                {
                    string fileExtension = Path.GetExtension(filePath);
                    string thumbFileName = string.Format("{0}_{1}{2}",
                        Path.GetFileNameWithoutExtension(filePath),
                        targetSize,
                        fileExtension);
                    if (!S3ExistsImageOnThumbs(thumbFileName))
                    {
                        //Read from S3
                        var pictureBinary = S3ReadAllBytes(defaultImageFileName);
                        using (var stream = new MemoryStream(pictureBinary))
                        {
                            Bitmap b = null;
                            try
                            {
                                //try-catch to ensure that picture binary is really OK. Otherwise, we can get "Parameter is not valid" exception if binary is corrupted for some reasons
                                b = new Bitmap(stream);
                            }
                            catch (ArgumentException exc)
                            {
                                _logger.Error("Error generating default picture",exc);
                            }

                            var newSize = CalculateDimensions(b.Size, targetSize);
                            try
                            {
                                var destStream = new MemoryStream();
                                ImageBuilder.Current.Build(b, destStream, new ResizeSettings()
                                {
                                    Width = newSize.Width,
                                    Height = newSize.Height,
                                    Scale = ScaleMode.Both,
                                    Quality = _mediaSettings.DefaultImageQuality
                                });
                                var destBinary = destStream.ToArray();
                                S3UploadImageOnThumbs(thumbFileName, destBinary, fileExtension);
                            }
                            catch
                            {

                            }
                            b.Dispose();
                        }
                    }
                    var url = GetThumbUrl(thumbFileName, storeLocation);
                    return url;
                }
                else
                {
                    string fileExtension = Path.GetExtension(filePath);
                    string thumbFileName = string.Format("{0}_{1}{2}",
                        Path.GetFileNameWithoutExtension(filePath),
                        targetSize,
                        fileExtension);
                    var thumbFilePath = GetThumbLocalPath(thumbFileName);
                    if (!File.Exists(thumbFilePath))
                    {
                        using (var b = new Bitmap(filePath))
                        {
                            var newSize = CalculateDimensions(b.Size, targetSize);

                            var destStream = new MemoryStream();
                            ImageBuilder.Current.Build(b, destStream, new ResizeSettings()
                            {
                                Width = newSize.Width,
                                Height = newSize.Height,
                                Scale = ScaleMode.Both,
                                Quality = _mediaSettings.DefaultImageQuality
                            });
                            var destBinary = destStream.ToArray();
                            File.WriteAllBytes(thumbFilePath, destBinary);
                        }
                    }
                    var url = GetThumbUrl(thumbFileName, storeLocation);
                    return url;
                }
            }
        }

        /// <summary>
        /// Get a picture URL
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="showDefaultPicture">A value indicating whether the default picture is shown</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <param name="defaultPictureType">Default picture type</param>
        /// <returns>Picture URL</returns>
        public new virtual string GetPictureUrl(int pictureId,
            int targetSize = 0,
            bool showDefaultPicture = true,
            string storeLocation = null,
            PictureType defaultPictureType = PictureType.Entity)
        {
            var picture = GetPictureById(pictureId);
            return GetPictureUrl(picture, targetSize, showDefaultPicture, storeLocation, defaultPictureType);
        }

        /// <summary>
        /// Get a picture URL(BASE) form original file store/S3
        /// </summary>
        /// <param name="picture">Picture instance</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="showDefaultPicture">A value indicating whether the default picture is shown</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <param name="defaultPictureType">Default picture type</param>
        /// <returns>Picture URL</returns>
        public virtual string GetPictureUrlBase(Picture picture,
            int targetSize = 0,
            bool showDefaultPicture = true,
            string storeLocation = null,
            PictureType defaultPictureType = PictureType.Entity)
        {
            if (picture == null)
            {
                if (showDefaultPicture)
                    return GetDefaultPictureUrl(targetSize, defaultPictureType, storeLocation);
                return "";
            }

            string thumbFileName;
            string thumbFilePath;
            string seoFileName = picture.SeoFilename;
            string lastPart = GetFileExtensionFromMimeType(picture.MimeType);

            if (targetSize == 0)
            {
                thumbFileName = !string.IsNullOrEmpty(seoFileName) ?
                                string.Format("{0}_{1}.{2}", picture.Id.ToString("0000000"), seoFileName, lastPart) :
                                string.Format("{0}.{1}", picture.Id.ToString("0000000"), lastPart);
            }
            else
            {
                thumbFileName = !string.IsNullOrEmpty(seoFileName) ?
                    string.Format("{0}_{1}_{2}.{3}", picture.Id.ToString("0000000"), seoFileName, targetSize, lastPart) :
                    string.Format("{0}_{1}.{2}", picture.Id.ToString("0000000"), targetSize, lastPart);
            }

            thumbFilePath = GetThumbLocalPath(thumbFileName);
            if (_mediaSettings.AWSS3Enable)
            {
                if (!S3ExistsImageOnThumbs(thumbFileName))
                {
                    return GetResizedPictureUrl(picture, targetSize, showDefaultPicture, storeLocation, defaultPictureType, thumbFileName, thumbFilePath, true);
                }
            }
            else
            {
                if (!File.Exists(thumbFilePath))
                {
                    return GetResizedPictureUrl(picture, targetSize, showDefaultPicture, storeLocation, defaultPictureType, thumbFileName, thumbFilePath, false);
                }
            }

            return thumbFilePath;
        }

        protected string GetResizedPictureUrl(Picture picture, 
            int targetSize, 
            bool showDefaultPicture, 
            string storeLocation, 
            PictureType defaultPictureType, 
            string thumbFileName,
            string thumbFilePath,
            bool s3Enabled)
        {
            byte[] pictureBinary = null;
            if (picture != null)
            {
                pictureBinary = LoadPictureBinary(picture);
            }
            if (picture == null || pictureBinary == null || pictureBinary.Length == 0)
            {
                if (showDefaultPicture)
                {
                    return GetDefaultPictureUrl(targetSize, defaultPictureType, storeLocation);
                }
                return "";
            }

            if (targetSize > 0)
            {
                using (var stream = new MemoryStream(pictureBinary))
                {
                    Bitmap b = null;
                    try
                    {
                        //try-catch to ensure that picture binary is really OK. Otherwise, we can get "Parameter is not valid" exception if binary is corrupted for some reasons
                        b = new Bitmap(stream);
                    }
                    catch (ArgumentException exc)
                    {
                        _logger.Error(string.Format("Error generating picture thumb. ID={0}", picture.Id), exc);
                    }
                    if (b == null)
                    {
                        //bitmap could not be loaded for some reasons
                        return "";
                    }

                    var newSize = CalculateDimensions(b.Size, targetSize);

                    var destStream = new MemoryStream();
                    ImageBuilder.Current.Build(b, destStream, new ResizeSettings()
                    {
                        Width = newSize.Width,
                        Height = newSize.Height,
                        Scale = ScaleMode.Both,
                        Quality = _mediaSettings.DefaultImageQuality
                    });
                    byte[] destBinary = destStream.ToArray();

                    if (s3Enabled)
                        S3UploadImageOnThumbs(thumbFileName, destBinary, picture.MimeType);
                    else
                        File.WriteAllBytes(thumbFilePath, destBinary);

                    b.Dispose();
                }
            }
            else
            {
                if (s3Enabled)
                    S3UploadImageOnThumbs(thumbFileName, pictureBinary, picture.MimeType);
                else
                    File.WriteAllBytes(thumbFilePath, pictureBinary);
            }
            return thumbFilePath;
        }


        /// <summary>
        /// Get Picture Url
        /// </summary>
        /// <param name="picture"></param>
        /// <param name="targetSize"></param>
        /// <param name="showDefaultPicture"></param>
        /// <param name="storeLocation"></param>
        /// <param name="defaultPictureType"></param>
        /// <returns></returns>
        public new virtual string GetPictureUrl(Picture picture,
            int targetSize = 0,
            bool showDefaultPicture = true,
            string storeLocation = null,
            PictureType defaultPictureType = PictureType.Entity)
        {
            string url = GetPictureUrlBase(picture, targetSize, showDefaultPicture, storeLocation, defaultPictureType);

            if (_mediaSettings.EnableCdn)
            {
                if (_mediaSettings.AWSS3Enable)
                    url = url                        
                        .Replace(_mediaSettings.AWSS3BucketName+"/", "")
                        .Replace(_mediaSettings.AWSS3RootUrl.TrimEnd('/'), _mediaSettings.CdnBaseUrl.TrimEnd('/'));
                else
                    url=url.Replace($"{_webHelper.GetStoreLocation()}content", _mediaSettings.CdnBaseUrl.TrimEnd('/'));
            }

            return url;
        }

        /// <summary>
        /// Get a picture local path
        /// </summary>
        /// <param name="picture">Picture instance</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="showDefaultPicture">A value indicating whether the default picture is shown</param>
        /// <returns></returns>
        public new virtual string GetThumbLocalPath(Picture picture, int targetSize = 0, bool showDefaultPicture = true)
        {
            string url = GetPictureUrlBase(picture, targetSize, showDefaultPicture);
            if (String.IsNullOrEmpty(url))
                return String.Empty;
            else
                return GetThumbLocalPath(Path.GetFileName(url));
        }


        /// <summary>
        /// Inserts a picture
        /// </summary>
        /// <param name="pictureBinary">The picture binary</param>
        /// <param name="mimeType">The picture MIME type</param>
        /// <param name="seoFilename">The SEO filename</param>
        /// <param name="isNew">A value indicating whether the picture is new</param>
        /// <param name="validateBinary">A value indicating whether to validated provided picture binary</param>
        /// <returns>Picture</returns>
        public new virtual Picture InsertPicture(byte[] pictureBinary, string mimeType, string seoFilename,
            string altAttribute = null, string titleAttribute = null,
            bool isNew = true, bool validateBinary = true)
        {
            mimeType = CommonHelper.EnsureNotNull(mimeType);
            mimeType = CommonHelper.EnsureMaximumLength(mimeType, 20);

            seoFilename = CommonHelper.EnsureMaximumLength(seoFilename, 100);

            if (validateBinary)
                pictureBinary = ValidatePicture(pictureBinary, mimeType);

            var picture = new Picture()
            {
                PictureBinary = this.StoreInDb ? pictureBinary : new byte[0],
                MimeType = mimeType,
                SeoFilename = seoFilename,
                IsNew = isNew,
            };
            _pictureRepository.Insert(picture);

            if (!this.StoreInDb)
                SavePictureInFile(picture.Id, pictureBinary, mimeType);

            //event notification
            _eventPublisher.EntityInserted(picture);

            return picture;
        }

        #endregion

        #region  AWS Helper

        /// <summary>
        /// Get S3 Image Path
        /// </summary>
        /// <returns></returns>
        public string GetS3ImagePath()
        {
            return $"{_mediaSettings.AWSS3BucketName}/images";
        }

        /// <summary>
        /// Get S3 image thumb path
        /// </summary>
        /// <returns></returns>
        public string GetS3ImageThumbPath()
        {
            return $"{_mediaSettings.AWSS3BucketName}/images/thumbs";
        }

        /// <summary>
        /// Get S3 image path (local)
        /// </summary>
        /// <returns></returns>
        public string GetS3ImageLocalPath()
        {
            return $"{_mediaSettings.AWSS3RootUrl}/{_mediaSettings.AWSS3BucketName}/images";
        }

        /// <summary>
        /// Get S3 thumb image path (local)
        /// </summary>
        /// <returns></returns>
        public string GetS3ImageThumbLocalPath()
        {
            return $"{_mediaSettings.AWSS3RootUrl}/{_mediaSettings.AWSS3BucketName}/images/thumbs";
        }

        /// <summary>
        /// Check S3 image exists or not
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public bool S3ExistsImage(string fileName)
        {
            try
            {
                using (var client= GetS3Client())
                {
                    var file = new S3FileInfo(client, GetS3ImagePath(), fileName);
                    return file.Exists;
                }
            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// Exists image on thumbs
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public bool S3ExistsImageOnThumbs(string fileName)
        {
            try
            {
                using (var client = GetS3Client())
                {
                    var file = new S3FileInfo(client, GetS3ImageThumbPath(), fileName);
                    return file.Exists;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ReadAllBytes form S3
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public byte[] S3ReadAllBytes(string fileName)
        {
            //Stream rs;
            using (var client = GetS3Client())
            {
                var getObjectRequest = new GetObjectRequest()
                {
                    BucketName = GetS3ImagePath(),
                    Key = fileName
                };

                using (var getObjectResponse = client.GetObjectAsync(getObjectRequest))
                {
                    using (var mutex = new Mutex(false))
                    {
                        using (Stream responseStream = getObjectResponse.Result.ResponseStream)
                        {
                            var bytes = S3ReadStream(responseStream);
                            return bytes;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Read image form thumbs
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public byte[] S3ReadAllBytesFromThumbs(string fileName)
        {
            //Stream rs;
            using (var client = GetS3Client())
            {
                var getObjectRequest = new GetObjectRequest()
                {
                    BucketName = GetS3ImageThumbPath(),
                    Key = fileName
                };

                using (var getObjectResponse = client.GetObjectAsync(getObjectRequest))
                {
                    using (Stream responseStream = getObjectResponse.Result.ResponseStream)
                    {
                        var bytes = S3ReadStream(responseStream);
                        return bytes;
                    }
                }
            }
        }

        /// <summary>
        /// Read Stream From S3
        /// </summary>
        /// <param name="responseStream"></param>
        /// <returns></returns>
        public byte[] S3ReadStream(Stream responseStream)
        {
            byte[] buffer = new byte[16 * 1024];
            using (var mutex = new Mutex(false))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    int read;
                    while ((read = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Upload image to S3
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="pictureBinary"></param>
        /// <param name="mimeType"></param>
        public void S3UploadImage(string fileName, byte[] pictureBinary, string mimeType)
        {
            try
            {
                var client = GetS3Client();
                var putRequest = new PutObjectRequest
                {
                    BucketName = GetS3ImagePath(),
                    Key = fileName,
                    InputStream = new MemoryStream(pictureBinary),
                    ContentType = mimeType,
                };
                putRequest.CannedACL = S3CannedACL.PublicRead;
                putRequest.Headers.Expires = DateTime.UtcNow.AddDays(_mediaSettings.ExpiresDays);

                PutObjectResponse response = client.PutObject(putRequest);
            }
            catch (AmazonS3Exception ex)
            {
                if (ex.ErrorCode != null && (ex.ErrorCode.Equals("InvalidAccessKeyId") || ex.ErrorCode.Equals("InvalidSecurity")))
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, ex.Message + " ## Check Crendentials", ex.StackTrace);
                }
                else
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, $"Error occurred. Message:{ex.Message} when writing an object", ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// Upload image on thumbs
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="pictureBinary"></param>
        /// <param name="mimeType"></param>
        public void S3UploadImageOnThumbs(string fileName, byte[] pictureBinary, string mimeType)
        {
            try
            {
                var client = GetS3Client();
                var putRequest = new PutObjectRequest
                {
                    BucketName = GetS3ImageThumbPath(),
                    Key = fileName,
                    InputStream = new MemoryStream(pictureBinary),
                    ContentType = mimeType,
                };
                putRequest.CannedACL = S3CannedACL.PublicRead;
                putRequest.Headers.Expires = DateTime.UtcNow.AddDays(_mediaSettings.ExpiresDays);
                PutObjectResponse response = client.PutObject(putRequest);
            }
            catch (AmazonS3Exception ex)
            {
                if (ex.ErrorCode != null && (ex.ErrorCode.Equals("InvalidAccessKeyId") || ex.ErrorCode.Equals("InvalidSecurity")))
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, ex.Message + " ## Check Crendentials", ex.StackTrace);
                }
                else
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, $"Error occurred. Message:{ex.Message} when writing an object", ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// S3 delete image form thumbs
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="mimeType"></param>
        public void S3DeleteImageFromThumbs(string fileName, string mimeType)
        {
            try
            {
                var client = GetS3Client();
                var deleteObjectRequest = new DeleteObjectRequest
                {
                    BucketName = GetS3ImageThumbPath(),
                    Key = fileName,
                };

                client.DeleteObject(deleteObjectRequest);
            }
            catch (AmazonS3Exception ex)
            {
                if (ex.ErrorCode != null && (ex.ErrorCode.Equals("InvalidAccessKeyId") || ex.ErrorCode.Equals("InvalidSecurity")))
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, ex.Message + " ## Check Crendentials", ex.StackTrace);
                }
                else
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, $"Error occurred. Message:{ex.Message} when writing an object", ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// S3 delete image
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="mimeType"></param>
        public void S3DeleteImage(string fileName, string mimeType)
        {
            try
            {
                var client = GetS3Client();
                var deleteObjectRequest = new DeleteObjectRequest
                {
                    BucketName =GetS3ImagePath(),
                    Key = fileName,
                };

                client.DeleteObject(deleteObjectRequest);
            }
            catch (AmazonS3Exception ex)
            {
                if (ex.ErrorCode != null && (ex.ErrorCode.Equals("InvalidAccessKeyId") || ex.ErrorCode.Equals("InvalidSecurity")))
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, ex.Message + " ## Check Crendentials", ex.StackTrace);
                }
                else
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, $"Error occurred. Message:{ex.Message} when writing an object", ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// Get AWS S3 Client with credentials
        /// </summary>
        /// <returns></returns>
        private AmazonS3Client GetS3Client()
        {
            return new AmazonS3Client(_mediaSettings.AWSS3AccessKeyId,_mediaSettings.AWSS3SecretKey,RegionEndpoint.APSoutheast1);
        }

        #endregion

    }
}
