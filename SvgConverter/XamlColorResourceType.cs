namespace SvgConverter
{
    using System;
    using System.Xml.Linq;

    public enum XamlColorResourceType
    {
        Color,
        Brush,
        Opacity
    }

    public static partial class XamlColorResourceTypeExtensions
    {
        public static string GetGlobalResourceKey(this XamlColorResourceType value)
            => value.ToString();
        //{
        //    switch (value)
        //    {
        //        case XamlColorResourceType.Color:
        //            return "Color";
        //        case XamlColorResourceType.Brush:
        //            return "ColorBrush";
        //        case XamlColorResourceType.Opacity:
        //            return "Opacity";
        //        default:
        //            throw new ArgumentOutOfRangeException(nameof(value), value, null);
        //    }
        //}
        public static XNamespace GetElementNamespace(this XamlColorResourceType value)
        {
            switch (value)
            {
                case XamlColorResourceType.Color:
                case XamlColorResourceType.Brush:
                    return ConverterLogic.nsDef;
                case XamlColorResourceType.Opacity:
                    return ConverterLogic.nsSys;
                    return "sys";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }
        }

        public static XName GetElementXName(this XamlColorResourceType value)
            => value.GetElementNamespace() + value.GetElementName();
        public static string GetElementName(this XamlColorResourceType value)
        {
            switch (value)
            {
                case XamlColorResourceType.Color:
                    return "Color";
                case XamlColorResourceType.Brush:
                    return "SolidColorBrush";
                case XamlColorResourceType.Opacity:
                    return "Double";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }
        }
        public static string GetLocalName(this XamlColorResourceType value, SvgConversionState state, string imageName)
            => state.HasMultipleImages ? (imageName + (state.XamlName == imageName ? "Local" : "")) : state.XamlName;
        public static string GetLocalResourceKey(this XamlColorResourceType value, SvgConversionState state, string imageName)
            => value.GetLocalName(state, imageName) + value.GetGlobalResourceKey();

        //{
        //    switch (value)
        //    {
        //        case XamlColorResourceType.Brush:
        //            return "BrushColor";
        //        case XamlColorResourceType.Color:
        //            return "Local" ;
        //        case XamlColorResourceType.Opacity:
        //            return "Opacity";
        //        default:
        //            throw new ArgumentOutOfRangeException(nameof(value), value, null);
        //    }
        //}
    }
}