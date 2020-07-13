namespace Nop.Services.Caching.Extension
{
    public static class CacheFilePath
    {
        public static string MegaMenu { get { return "megamenu"; } }
        public static string PartnerTop { get { return "partnertop"; } }
        public static string QuickGoLink { get { return "quickgolink"; } }
        public static string HomePageSpecialCategory { get { return "homepage_specialcategory"; } }
        public static string SpecialCategoryOld { get { return "special_category_old"; } }
        public static string HomePageSliderBottom { get { return "homepage_sliderbottom"; } }
        public static string HomePageLeftNavigation { get { return "homepage_leftnav"; } }
        public static string OthobaSlideShowDesktop { get { return "homepage_slideshow_desktop"; } }
        public static string OthobaSlideShowMobile { get { return "homepage_slideshow_mobile"; } }
        public static string HomePageOnLoadRecommendedBlock { get { return "homepage_onload_recommended"; } }
    }

    public static class S3FileCacheArray
    {
        public static string OthobaCategoryNavLeft { get { return "category_nav"; } }
    }
}