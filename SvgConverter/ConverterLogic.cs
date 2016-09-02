using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace SvgConverter
{
    using System.Diagnostics;
    using System.Globalization;
    public class SvgConversionState
    {
        public SvgConverterOptions Options;
        public int Total;
        public string XamlName => Options.XamlName;
        public bool HasMultipleImages => Total > 1;
    }
    public static class ConverterLogic
    {
        static ConverterLogic()
        {
            _nsManager.AddNamespace("defns", nsDef.NamespaceName);
            _nsManager.AddNamespace("x", nsx.NamespaceName);
            _nsManager.AddNamespace("sys", nsSys.NamespaceName);

        }

        public static string GetNormalizedXamlName(string xamlName, string replacement="")
        {

            xamlName = ValidateName(xamlName, replacement);
            var firstChar = Char.ToUpper(xamlName[0]);
            return firstChar + xamlName.Remove(0, 1);
        }
        internal static XNamespace nsx = "http://schemas.microsoft.com/winfx/2006/xaml";
        internal static XNamespace nsDef = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        internal static XNamespace nsSys = "clr-namespace:System;assembly=mscorlib";
        internal static XmlNamespaceManager _nsManager = new XmlNamespaceManager(new NameTable());
        internal static XDocument Document;
        internal static bool addedSysNamespace = false;
        public static string SvgFileToXaml(string filepath,
            ResultMode resultMode,
            WpfDrawingSettings wpfDrawingSettings = null, SvgConverterOptions options=null)
        {
            options = options ?? new SvgConverterOptions();
            string name;
            var obj = ConvertSvgToObject(filepath, resultMode, wpfDrawingSettings, options, out name);
            return SvgObjectToXaml(obj, wpfDrawingSettings != null ? wpfDrawingSettings.IncludeRuntime : false, name);
        }

        public static ConvertedSvgData ConvertSvg(string filepath, ResultMode resultMode)
        {
            //Lazy Loading: all these elements are loaded if someone accesses the getter
            //string name;
            //var obj = ConvertSvgToObject(filepath, resultMode, null, out name) as DependencyObject;
            //var xaml = SvgObjectToXaml(obj, false, name);
            //var svg = File.ReadAllText(filepath);
           
            return new ConvertedSvgData { Filepath = filepath
            //, ConvertedObj = obj, Svg = svg, Xaml = xaml 
            };
        }

        public static object ConvertSvgToObject(string filepath, ResultMode resultMode, WpfDrawingSettings wpfDrawingSettings, SvgConverterOptions options, out string name)
        {
            var dg = ConvertFileToDrawingGroup(filepath, wpfDrawingSettings);
            switch (resultMode)
            {
                case ResultMode.DrawingGroup:
                    name = BuildDrawingGroupName(filepath, options);
                    return dg;
                case ResultMode.DrawingImage:
                    name = BuildDrawingImageName(filepath, options);
                    return DrawingToImage(dg);
                default:
                    throw new ArgumentOutOfRangeException("resultMode");
            }
        }

        public static string SvgObjectToXaml(object obj, bool includeRuntime, string name)
        {
            var xamlUntidy = WpfObjToXaml(obj, includeRuntime);

            var doc = XDocument.Parse(xamlUntidy);
            BeautifyDrawingElement(doc.Root, name);
            var xamlWithNamespaces = doc.ToString();

            var xamlClean = RemoveNamespaceDeclarations(xamlWithNamespaces);
            return xamlClean;
        }

        public static string SvgDirToXaml(string folder, string xamlName)
        {
            return SvgDirToXaml(folder, null, new SvgConverterOptions {FileName = xamlName});
        }

        public static string SvgDirToXaml(string folder, WpfDrawingSettings wpfDrawingSettings,
                                          SvgConverterOptions options = null)
        {
            string content;
            SvgDirToXaml(folder, options, out content, wpfDrawingSettings);
            return content;
        }
        public static bool SvgDirToXaml(string folder, SvgConverterOptions options, out string content, WpfDrawingSettings wpfDrawingSettings=null)
        {
            //TODO: Implement and consolidate options

            var files = SvgFilesFromFolder(folder);
            var dict = ConvertFilesToResourceDictionary(files, wpfDrawingSettings, options);
            var xamlUntidy = WpfObjToXaml(dict, wpfDrawingSettings?.IncludeRuntime ?? false);

            Document = XDocument.Parse(xamlUntidy);
            addedSysNamespace = false;
            RemoveResDictEntries(Document.Root);
            var drawingGroupElements = Document.Root.XPathSelectElements("defns:DrawingGroup", _nsManager).ToList();
            var state = new SvgConversionState
            {
                Options = options,
                Total = drawingGroupElements.Count
            };
            if (state.Total == 0)
            {
                content = Document.ToString();
                return false;
            }
            foreach (var drawingGroupElement in drawingGroupElements)
            {
                BeautifyDrawingElement(drawingGroupElement, null);
                ExtractGeometries(drawingGroupElement);
            }
            ReplaceResourcesInDrawingGroups(Document.Root, state);
            AddDrawingImagesToDrawingGroups(Document.Root, options);
            content = Document.ToString();
            return true;
        }

        public static IEnumerable<string> SvgFilesFromFolder(string folder)
        {
            try
            {
                return Directory.GetFiles(folder, "*.svg*");
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static readonly XamlColorResourceType[] ColorResourceTypes =
        {
            XamlColorResourceType.Brush,
            XamlColorResourceType.Color,
            XamlColorResourceType.Opacity
        };
        
        private static void ReplaceResourcesInDrawingGroups(XElement rootElement, SvgConversionState state)
        {
            //three steps of colouring: 1. global Color, 2, global ColorBrush, 3. local ColorBrush
            //<Color x:Key="ImagesColor1">#FF000000</Color>
            //<SolidColorBrush x:Key="ImagesColorBrush1" Color="{DynamicResource ImagesColor1}" />
            //<SolidColorBrush x:Key="JOG_BrushColor1" Color="{Binding Color, Source={StaticResource ImagesColorBrush1}}" />
            var distinctResources = new(string[] Colors, string[] Brushes, string[] Opacities)
            {
                Colors = null,
                Brushes = CollectBrushAttributesWithColor(rootElement)
                    .Select(a => a.Value)
                    //same Color only once
                    .Distinct(StringComparer.InvariantCultureIgnoreCase)
                    .ToArray(),
                Opacities = CollectOpacityAttributesFromBrushElements(rootElement)
                    .Select(a => Convert.ToDouble(a.Value))
                    .Where(x => x != 1)
                    //same Opacity only once
                    .Distinct()
                    .Select(x => x.ToString(CultureInfo.InvariantCulture))
                    .ToArray()
            };

            distinctResources.Colors = distinctResources.Brushes
                .Concat(CollectColorAttributesFromBrushElements(rootElement)
                            .Select(a => a.Value))
                //same Color only once
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToArray();

            if (!addedSysNamespace && distinctResources.Opacities.Any())
            {
                addedSysNamespace = true;
                Document.Root.Add(new XAttribute(XNamespace.Xmlns + "sys", nsSys.NamespaceName));
            }

            var drawingGroups = rootElement.Elements(nsDef + "DrawingGroup").ToList();
            foreach (var resourceType in ColorResourceTypes)
            {
                string[] distinctResourceValues;
                switch (resourceType)
                {
                    case XamlColorResourceType.Color:
                        distinctResourceValues = distinctResources.Colors;
                        break;
                    case XamlColorResourceType.Brush:
                        distinctResourceValues = distinctResources.Brushes;
                        break;
                    case XamlColorResourceType.Opacity:
                        distinctResourceValues = distinctResources.Opacities;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
                }
                ReplaceResourcesInDrawingGroup(rootElement, state, drawingGroups, distinctResourceValues, resourceType);
            }
        }

        private static void ReplaceResourcesInDrawingGroup(XElement rootElement, SvgConversionState state, List<XElement> drawingGroups,
                                     string[] distinctResourceValues, XamlColorResourceType resourceType)
        {
            // Building Elements like: 1) <SolidColorBrush x:Key="ImagesColorBrush1" Color="{DynamicResource ImagesColor1}" />
            //                         2) <Color x:Key="ImagesColor1">#FF000000</Color>
            //                         3) <sys:Double x:Key="ImagesOpacity1">0.90</sys:Double>

            var isSingleGlobalResource = distinctResourceValues.Length < 2;
            var resources = distinctResourceValues
                .Select((s, i) => new XamlColorResource(isSingleGlobalResource, state.XamlName, i, s))
                .ToList();
            rootElement.AddFirst(resources
                                     .Select(brush => brush.GetXElement(resourceType)));
            foreach (var node in drawingGroups)
            {
                ReplaceResourceInDrawingGroups(state, node, resources, resourceType);
            }
        }

        private static string GetNameFromDrawingGroupKey(string nameDg)
            => nameDg.Substring(0, nameDg.Length - nameof(DrawingGroup).Length);
        private static void ReplaceResourceInDrawingGroups(SvgConversionState state, XElement node, List<XamlColorResource> resources,
                                                         XamlColorResourceType resourceType = XamlColorResourceType.Brush)
        {
            //get Name of DrawingGroup
            var isBrush = resourceType == XamlColorResourceType.Brush;
            var nameDg = node.Attribute(nsx + "Key").Value;
            var name = GetNameFromDrawingGroupKey(nameDg);
            var localResourceKey = resourceType.GetLocalResourceKey(state, name);
            var elementName = resourceType.GetGlobalResourceKey();
            var localElementType = resourceType.GetElementName();
            //var localElementAttribute = nameof(Color);
            var resourceKeys = resources.ToDictionary(brush => brush.Value, brush => brush.GetResourceKey(resourceType));
            var attributes = CollectColorResourceAttributes(node, resourceType).ToList();
            var addedGlobalElements = new List<string>();
            var distinctLocalElements = attributes.Select(x => x.Value).Distinct().Count();
            var isSingleResource = state.Options.ConsolidateBrushes
                ? (resourceKeys.Count <= 1 && distinctLocalElements <= 1)
                : attributes.Count <= 1;
            for (int index = 0, total = attributes.Count; index < total; index++)
            {
                var attribute = attributes[index];
                var color = attribute.Value;
                string resKey;
                if (resourceKeys.TryGetValue(color, out resKey))
                {
                    //global color found
                    string localName = null;
                    int resKeyNumber; 
                    var parsedResKey = int.TryParse(resKey.Substring(state.XamlName.Length + elementName.Length),
                                                    out resKeyNumber);
                    var addLocalResource = true;
                    if (distinctLocalElements == 1 || resourceKeys.Count == 1)
                    {
                        //if (!parsedResKey || !isBrush) { }
                        resKeyNumber = 0;
                        parsedResKey = true;
                    }
                    //build resourcename
                    if (state.Options.ConsolidateBrushes)
                    {
                        if (parsedResKey && resKeyNumber == 1 && isSingleResource)
                        {
                            resKeyNumber = 0;
                        }
                        Debug.Assert(parsedResKey, nameof(parsedResKey));
                        if (addedGlobalElements.Contains(resKey) || !state.HasMultipleImages)
                        {
                            addLocalResource = false;
                        }
                        else
                        {
                            addedGlobalElements.Add(resKey);                            
                        }
                        if (state.HasMultipleImages && parsedResKey && resKeyNumber > 0)
                        {
                            resKeyNumber = addedGlobalElements.IndexOf(resKey) + 1;
                        }
                    }
                    else
                    {
                        //dont add number if only one resource
                        resKeyNumber = isSingleResource
                            ? 0
                            : index + 1;
                    }
                    localName = $"{localResourceKey}{(resKeyNumber > 0 ? resKeyNumber.ToString() : "")}";
                    //TODO: Temporarily skipping refactoring for non-brush resources... Update proxy for non-brush resources and remove the following line
                    if (!isBrush)
                        localName = resKey;
                    attribute.Value = "{DynamicResource " + localName + "}";

                    if (!addLocalResource)
                        continue;
                    //TODO: Temporarily skipping refactoring for non-brush resources... Update proxy for non-brush resources and remove the following line
                    if (!isBrush)
                        continue;
                    XAttribute newElementAttribute;
                    var newElement = new XElement(nsDef + (isBrush
                                                      ? localElementType
                                                      : nameof(DynamicResourceExtension)),
                                                  new XAttribute(nsx + "Key", localName));

                    if (!addLocalResource)
                        continue;
                    switch (resourceType)
                    {
                        case XamlColorResourceType.Opacity:
                        case XamlColorResourceType.Color:
                            //TODO: Update this code, since we cannot use a DynamicResource as a proxy for the original resource.
                            newElementAttribute = new XAttribute(nameof(DynamicResourceExtension.ResourceKey),
                                                                 resKey);
                            break;
                        case XamlColorResourceType.Brush:
                            newElementAttribute = new XAttribute(nameof(Color),
                                                                 $"{{Binding Color, Source={{StaticResource {resKey}}}}}");
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
                    }
                    newElement.Add(newElementAttribute);
                    node.AddBeforeSelf(newElement);

                }
            }
        }

        private static IEnumerable<XAttribute> CollectColorResourceAttributes(XElement drawingElement,
                                                                              XamlColorResourceType resourceType)
        {
            switch (resourceType)
            {
                case XamlColorResourceType.Color:
                    return CollectColorAttributesFromBrushElements(drawingElement);
                case XamlColorResourceType.Brush:
                    return CollectBrushAttributesWithColor(drawingElement);
                case XamlColorResourceType.Opacity:
                    return CollectOpacityAttributesFromBrushElements(drawingElement);
                default:
                    throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
            }
        }

        private static IEnumerable<XAttribute> CollectAttributesFromBrushElements(XElement drawingElement)
            => drawingElement.Descendants()
                .Where(d => d.Name.LocalName == "SolidColorBrush")
                .SelectMany(d => d.Attributes());

        private static IEnumerable<XAttribute> CollectOpacityAttributesFromBrushElements(XElement drawingElement)
            => CollectAttributesFromBrushElements(drawingElement)
                .Where(a => a.Name.LocalName == "Opacity");

        private static IEnumerable<XAttribute> CollectColorAttributesFromBrushElements(XElement drawingElement)
            => CollectAttributesFromBrushElements(drawingElement)
                //is Color like #FF000000
                .Where(a => a.Name.LocalName == "Color" && a.Value.StartsWith("#"));

        private static IEnumerable<XAttribute> CollectBrushAttributesWithColor(XElement drawingElement)
            => drawingElement.Descendants()
                .SelectMany(d => d.Attributes())
                .Where(a =>
                           (a.Name.LocalName == "Brush" || a.Name.LocalName == "ForegroundBrush")
                           //is Color like #FF000000
                           && a.Value.StartsWith("#"));

        private static void AddDrawingImagesToDrawingGroups(XElement rootElement, SvgConverterOptions options)
        {
            var drawingGroups = rootElement.Elements(nsDef + "DrawingGroup").ToList();
            foreach (var node in drawingGroups)
            {
                //get Name of DrawingGroup
                var nameDg = node.Attribute(nsx + "Key").Value;
                var name = GetNameFromDrawingGroupKey(nameDg);
                var nameImg = name + options.DrawingImageKey;
                //<DrawingImage x:Key="xxx" Drawing="{StaticResource cloud_5_icon_DrawingGroup}"/>
                var drawingImage = new XElement(nsDef + "DrawingImage",
                    new XAttribute(nsx + "Key", nameImg),
                    new XAttribute("Drawing", $"{{StaticResource {nameDg}}}")
                    );
                node.AddAfterSelf(drawingImage);
            }
        }

        internal static ResourceDictionary ConvertFilesToResourceDictionary(IEnumerable<string> files, WpfDrawingSettings wpfDrawingSettings, SvgConverterOptions options)
        {
            var dict = new ResourceDictionary();
            foreach (var file in files)
            {
                var drawingGroup = ConvertFileToDrawingGroup(file, wpfDrawingSettings);
                var keyDg = BuildDrawingGroupName(file, options);
                dict[keyDg] = drawingGroup;
            }
            return dict;
        }

        private static DrawingGroup ConvertFileToDrawingGroup(string filepath, WpfDrawingSettings wpfDrawingSettings)
        {
            var dg = SvgFileToWpfObject(filepath, wpfDrawingSettings);
            SetSizeToGeometries(dg);
            RemoveObjectNames(dg);
            return dg;
        }

        internal static void SetSizeToGeometries(DrawingGroup dg)
        {
            var size = GetSizeFromDrawingGroup(dg);
            if (size.HasValue)
            {
                var geometries = GetPathGeometries(dg).ToList();
                geometries.ForEach(g => SizeGeometry(g, size.Value));
            }
        }

        public static IEnumerable<PathGeometry> GetPathGeometries(Drawing drawing)
        {
            var result = new List<PathGeometry>();

            Action<Drawing> HandleDrawing = null;
            HandleDrawing = aDrawing =>
            {
                if (aDrawing is DrawingGroup)
                    foreach (Drawing d in ((DrawingGroup)aDrawing).Children)
                    {
                        HandleDrawing(d);
                    }
                if (aDrawing is GeometryDrawing)
                {
                    var gd = (GeometryDrawing)aDrawing;
                    Geometry geometry = gd.Geometry;
                    if (geometry is PathGeometry)
                    {
                        result.Add((PathGeometry)geometry);
                    }
                }
            };
            HandleDrawing(drawing);

            return result;
        }

        public static void SizeGeometry(PathGeometry pg, Size size)
        {
            if (size.Height > 0 && size.Height > 0)
            {
                PathFigure[] sizeFigures =
                {
                    new PathFigure(new Point(size.Width, size.Height), Enumerable.Empty<PathSegment>(), true),
                    new PathFigure(new Point(0,0), Enumerable.Empty<PathSegment>(), true),
                };

                var newGeo = new PathGeometry(sizeFigures.Concat(pg.Figures), pg.FillRule, null);//pg.Transform do not add transform here, it will recalculate all the Points
                pg.Clear();
                pg.AddGeometry(newGeo);
                //return new PathGeometry(sizeFigures.Concat(pg.Figures), pg.FillRule, pg.Transform);
            }
        }

        internal static DrawingGroup SvgFileToWpfObject(string filepath, WpfDrawingSettings wpfDrawingSettings)
        {
            if (wpfDrawingSettings == null) //use defaults if null
                wpfDrawingSettings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = false, OptimizePath = true };
            var reader = new FileSvgReader(wpfDrawingSettings);

            //this is straight forward, but in this version of the dlls there is an error when name starts with a digit
            //var uri = new Uri(Path.GetFullPath(filepath));
            //reader.Read(uri); //accessing using the filename results is problems with the uri (if the dlls are packed in ressources)
            //return reader.Drawing;

            //this should be faster, but using CreateReader will loose text items like "JOG" ?!
            //using (var stream = File.OpenRead(Path.GetFullPath(filepath)))
            //{
            //    //workaround: error when Id starts with a number
            //    var doc = XDocument.Load(stream);
            //    ReplaceIdsWithNumbers(doc.Root); //id="3d-view-icon" -> id="_3d-view-icon"
            //    using (var xmlReader = doc.CreateReader())
            //    {
            //        reader.Read(xmlReader);
            //        return reader.Drawing;
            //    }
            //}

            //workaround: error when Id starts with a number
            var doc = XDocument.Load(Path.GetFullPath(filepath));
            ReplaceIdsWithNumbers(doc.Root); //id="3d-view-icon" -> id="_3d-view-icon"
            using (var ms = new MemoryStream())
            {
                doc.Save(ms);
                ms.Position = 0;
                reader.Read(ms);
                return reader.Drawing;
            }
        }

        private static void ReplaceIdsWithNumbers(XElement root)
        {
            var idAttributesStartingWithDigit = root.DescendantsAndSelf()
                .SelectMany(d=>d.Attributes())
                .Where(a=>string.Equals(a.Name.LocalName, "Id", StringComparison.InvariantCultureIgnoreCase))
                .Where(a=>char.IsDigit(a.Value.FirstOrDefault()));
            foreach (var attr in idAttributesStartingWithDigit)
            {
                attr.Value = "_" + attr.Value;
            }
        }

        internal static DrawingImage DrawingToImage(Drawing drawing)
        {
            return new DrawingImage(drawing);
        }

        internal static string WpfObjToXaml(object wpfObject, bool includeRuntime)
        {
            XmlXamlWriter writer = new XmlXamlWriter(new WpfDrawingSettings { IncludeRuntime = includeRuntime});            
            var xaml = writer.Save(wpfObject);        
            return xaml;
        }

        private static void RemoveResDictEntries(XElement root)
        {
            var entriesElem = root.Element(nsDef + "ResourceDictionary.Entries");
            if (entriesElem != null)
            {
                root.Add(entriesElem.Elements());
                entriesElem.Remove();
            }
        }

        private static void BeautifyDrawingElement(XElement drawingElement, string name)
        {
            InlineClipping(drawingElement);
            RemoveCascadedDrawingGroup(drawingElement);
            CollapsePathGeometries(drawingElement);
            SetDrawingElementxKey(drawingElement, name);
        }

        private static void InlineClipping(XElement drawingElement)
        {
            Rect clipRect;
            var clipElement = GetClipElement(drawingElement, out clipRect);
            if (clipElement != null && clipElement.Parent.Name.LocalName == "DrawingGroup")
            {   //add Attribute: ClipGeometry="M0,0 V40 H40 V0 H0 Z" this is the description of a rectangle-like Geometry
                clipElement.Parent.Add(new XAttribute("ClipGeometry", string.Format("M{0},{1} V{2} H{3} V{0} H{1} Z", clipRect.Left, clipRect.Top, clipRect.Right, clipRect.Bottom)));
                //delete the old Element
                clipElement.Remove();
            }
        }

        private static void RemoveCascadedDrawingGroup(XElement drawingElement)
        {
            //wenn eine DrawingGroup nix anderes wie eine andere DrawingGroup hat, werden deren Elemente eine Ebene hochgezogen und die überflüssige Group entfernt
            var drawingGroups = drawingElement.DescendantsAndSelf(nsDef + "DrawingGroup");
            foreach (var drawingGroup in drawingGroups)
            {
                var elems = drawingGroup.Elements().ToList();
                if (elems.Count == 1 && elems[0].Name.LocalName == "DrawingGroup")
                {
                    var subGroup = elems[0];

                    //var subElems = subGroup.Elements().ToList();
                    //subElems.Remove();
                    //drawingGroup.Add(subElems);
                    var subAttrNames = subGroup.Attributes().Select(a => a.Name);
                    var attrNames = drawingGroup.Attributes().Select(a => a.Name);
                    if (subAttrNames.Intersect(attrNames).Any())
                        return;
                    drawingGroup.Add(subGroup.Attributes());
                    drawingGroup.Add(subGroup.Elements());
                    subGroup.Remove();
                }
            }
        }

        private static void CollapsePathGeometries(XElement drawingElement)
        {
            //<DrawingGroup x:Name="cloud_3_icon_DrawingGroup" ClipGeometry="M0,0 V512 H512 V0 H0 Z">
            //  <GeometryDrawing Brush="#FF000000">
            //    <GeometryDrawing.Geometry>
            //      <PathGeometry FillRule="Nonzero" Figures="M512,512z M0,0z M409.338,216.254C398.922,161.293 z" />
            //    </GeometryDrawing.Geometry>
            //  </GeometryDrawing>
            //</DrawingGroup>

            //würde auch gehen:var pathGeometries = drawingElement.XPathSelectElements(".//defns:PathGeometry", _nsManager).ToArray();
            var pathGeometries = drawingElement.Descendants(nsDef + "PathGeometry").ToArray();
            foreach (var pathGeometry in pathGeometries)
            {
                if (pathGeometry.Parent != null && pathGeometry.Parent.Parent != null && pathGeometry.Parent.Parent.Name.LocalName == "GeometryDrawing")
                {
                    //check if only FillRule and Figures is available
                    var attrNames = pathGeometry.Attributes().Select(a => a.Name.LocalName).ToList();
                    if (attrNames.Count <= 2 && attrNames.Contains("Figures") && (attrNames.Contains("FillRule") || attrNames.Count == 1))
                    {
                        var sFigures = pathGeometry.Attribute("Figures").Value;
                        var fillRuleAttr = pathGeometry.Attribute("FillRule");
                        if (fillRuleAttr != null)
                        {
                            if (fillRuleAttr.Value == "Nonzero")
                                sFigures = "F1 " + sFigures; //Nonzero
                            else
                                sFigures = "F0 " + sFigures; //EvenOdd
                        }
                        pathGeometry.Parent.Parent.Add(new XAttribute("Geometry", sFigures));
                        pathGeometry.Parent.Remove();
                    }
                }
            }
        }

        private static void SetDrawingElementxKey(XElement drawingElement, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            var attributes = drawingElement.Attributes().ToList();
            attributes.Insert(0, new XAttribute(nsx + "Key", name)); //place in first position
            drawingElement.ReplaceAttributes(attributes);
        }

        private static void ExtractGeometries(XElement drawingGroupElement)
        {
            //get Name of DrawingGroup
            var nameDg = drawingGroupElement.Attribute(nsx + "Key").Value;
            var name = GetNameFromDrawingGroupKey(nameDg);

            //find this: <GeometryDrawing Brush="{DynamicResource _3d_view_icon_BrushColor}" Geometry="F1 M512,512z M0,0z M436.631,207.445L436.631,298.319z" />
            //var geos = drawingGroupElement.XPathSelectElements(".//defns:GeometryDrawing/@defns:Geometry", _nsManager).ToList();
            var geos = drawingGroupElement.Descendants()
                .Where(e => e.Name.LocalName == "GeometryDrawing")
                .SelectMany(e => e.Attributes())
                .Where(a => a.Name.LocalName == "Geometry")
                .ToList();
            foreach (var geo in geos)
            {
                //build resourcename
                var localName = geos.Count > 1
                    ? $"{name}Geometry{geos.IndexOf(geo) + 1}"
                    : $"{name}Geometry"; //dont add number if only one Geometry
                //Add this: <Geometry x:Key="cloud_3_iconGeometry">F1 M512,512z M0,0z M409.338,216.254C398.922,351.523z</Geometry>
                drawingGroupElement.AddBeforeSelf(new XElement(nsDef+"Geometry",
                    new XAttribute(nsx + "Key", localName),
                    geo.Value));
                geo.Value = $"{{StaticResource {localName}}}";
            }
        }

        public static string RemoveNamespaceDeclarations(String xml)
        {
            //hier wird nur die Deklaration des NS rausgeschmissen (rein auf StringBasis), so dass man den Kram pasten kann
            xml = xml.Replace(" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"", "");
            xml = xml.Replace(" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", "");
            return xml;
        }

        public static void RemoveObjectNames(DrawingGroup drawingGroup)
        {
            if (drawingGroup.GetValue(FrameworkElement.NameProperty) != null)
                drawingGroup.SetValue(FrameworkElement.NameProperty, null);
            foreach (var child in drawingGroup.Children.OfType<DependencyObject>())
            {
                if (child.GetValue(FrameworkElement.NameProperty) != null)
                    child.SetValue(FrameworkElement.NameProperty, null);
                if (child is DrawingGroup)
                    RemoveObjectNames(child as DrawingGroup);
            }
        }

        internal static string BuildDrawingGroupName(string filename, SvgConverterOptions options)
        {
            var rawName = Path.GetFileNameWithoutExtension(filename) + "DrawingGroup";
            return ValidateName(rawName, options.DrawingKeyReplacementString);
        }
        internal static string BuildDrawingImageName(string filename, SvgConverterOptions options)
        {
            var rawName = Path.GetFileNameWithoutExtension(filename) + options.DrawingImageKey;
            return ValidateName(rawName, options.DrawingKeyReplacementString);
        }
        internal static string ValidateName(string name, string keyReplacementString)
        {
            var result = Regex.Replace(name, @"[^[0-9a-zA-Z]]*", keyReplacementString);
            if (Regex.IsMatch(result, "^[0-9].*"))
                result = "_" + result;
            return result;
        }

        internal static void SetRootElementname(DependencyObject drawingGroup, string name)
        {
            drawingGroup.SetValue(FrameworkElement.NameProperty, name);
        }

        internal static XElement GetClipElement(XElement drawingGroupElement, out Rect rect)
        {
            rect = default(Rect);
            if (drawingGroupElement == null)
                return null;
            //<DrawingGroup x:Key="cloud_3_icon_DrawingGroup">
            //   <DrawingGroup>
            //       <DrawingGroup.ClipGeometry>
            //           <RectangleGeometry Rect="0,0,512,512" />
            //       </DrawingGroup.ClipGeometry>
            var clipElement = drawingGroupElement.XPathSelectElement("//defns:DrawingGroup.ClipGeometry", _nsManager);
            if (clipElement != null)
            {
                var rectangleElement = clipElement.Element(nsDef + "RectangleGeometry");
                if (rectangleElement != null)
                {
                    var rectAttr = rectangleElement.Attribute("Rect");
                    if (rectAttr != null)
                    {
                        rect = Rect.Parse(rectAttr.Value);
                        return clipElement;
                    }
                }
            }
            return null;
        }

        internal static Size? GetSizeFromDrawingGroup(DrawingGroup drawingGroup)
        {
            //<DrawingGroup x:Key="cloud_3_icon_DrawingGroup">
            //   <DrawingGroup>
            //       <DrawingGroup.ClipGeometry>
            //           <RectangleGeometry Rect="0,0,512,512" />
            //       </DrawingGroup.ClipGeometry>
            if (drawingGroup != null)
            {
                var subGroup = drawingGroup.Children
                    .OfType<DrawingGroup>()
                    .FirstOrDefault(c => c.ClipGeometry != null);
                if (subGroup != null)
                {
                    return subGroup.ClipGeometry.Bounds.Size;
                }
            }
            return null;
        }
    }
}
