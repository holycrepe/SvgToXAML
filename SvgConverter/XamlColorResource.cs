namespace SvgConverter
{
    using System;
    using System.Xml.Linq;

    public class XamlColorResource
    {
        public readonly string ColorKey;
        public readonly string BrushKey;
        public readonly string OpacityKey;
        public readonly string Value;

        public XamlColorResource(bool isSingle, string xamlName, int number, string color)
        {
            var suffix = isSingle
                ? ""
                : (number + 1).ToString();
            Func<XamlColorResourceType, string> getResourceKey =
                colorResourceType => $"{xamlName}{colorResourceType.GetGlobalResourceKey()}{suffix}";
            ColorKey = getResourceKey(XamlColorResourceType.Color);
            BrushKey = getResourceKey(XamlColorResourceType.Brush);
            OpacityKey = getResourceKey(XamlColorResourceType.Opacity);
            Value = color;
        }

        public string GetResourceKey(XamlColorResourceType resourceType)
        {
            switch (resourceType)
            {
                case XamlColorResourceType.Color:
                    return ColorKey;                    
                case XamlColorResourceType.Brush:
                    return BrushKey;
                case XamlColorResourceType.Opacity:
                    return OpacityKey;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
            }
        }

        public XElement GetXElement(XamlColorResourceType resourceType)
        {
            var element = new XElement(resourceType.GetElementXName(),
                                       new XAttribute(ConverterLogic.nsx + "Key", GetResourceKey(resourceType)));
            switch (resourceType)
            {
                case XamlColorResourceType.Color:
                case XamlColorResourceType.Opacity:
                    element.Add(Value);
                    break;
                case XamlColorResourceType.Brush:
                    element.Add(new XAttribute("Color", $"{{DynamicResource {ColorKey}}}"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
            }
            return element;
        }
    }
}