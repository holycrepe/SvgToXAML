namespace SvgConverter
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    public class SvgConverterOptions
    {
        private string _xamlName;
        private string _outputFileFormat;
        private string[] _sourceDirectories = null;
        private bool _recurse;
        private string _source;
        private SearchOption _recursionDepth = SearchOption.TopDirectoryOnly;
        private string _target;
        private string _outputNameFormat;
        public bool ConsolidateBrushes { get; set; } = true;
        public string DrawingImageKey { get; set; } = "Image";
        public string MainKeyReplacementString { get; set; } = "";
        public string DrawingKeyReplacementString { get; set; } = "";

        public string XamlName
        {
            get
            {
                return _xamlName ??
                       (_xamlName = ConverterLogic.GetNormalizedXamlName(FileName, MainKeyReplacementString));
            }
            set { _xamlName = value; }
        }

        public string FileName { get; set; }
        public string OutputName { get; private set; }
        public string OutputFileName { get; private set; }
        public FileInfo OutputFile { get; private set; }
        public string SourceSubPath { get; private set; }
        public string SourceName { get; private set; }

        public string OutputNameFormat
        {
            get { return string.IsNullOrWhiteSpace(_outputNameFormat) ? "{2}" : _outputNameFormat; }
            set { _outputNameFormat = value; }
        }

        public string OutputFileFormat
        {
            get
            {
                if (_outputFileFormat == null)
                {
                    _outputFileFormat = Path.Combine(Target, string.IsNullOrWhiteSpace(_outputNameFormat) ? "{1}" : OutputFileFormat);
                    if (!Path.HasExtension(_outputFileFormat))
                    {
                        _outputFileFormat = Path.ChangeExtension(_outputFileFormat, ".xaml");
                    }
                }
                return _outputFileFormat;
            }
            set { _outputFileFormat = value; }
        }

        public string Source
        {
            get { return _source; }
            set
            {
                _source = string.IsNullOrWhiteSpace(value) ? "." : value;
                _sourceDirectories = null;
                var root = Path.GetFullPath(_source);
                root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                RootDirectory = new DirectoryInfo(root);
            }
        }

        public string Target
        {
            get { return _target ?? Source; }
            set { _target = string.IsNullOrWhiteSpace(value) ? null : value; }
        }

        string FormatPath(string path)
            => string.Format(path, RootDirectory.FullName, SourceSubPath, SourceName);
        public void SetDirectory(string directory)
        {
            SourceName = Path.GetFileName(directory);
            SourceSubPath = directory.Substring(RootDirectory.FullName.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            OutputName = FormatPath(OutputNameFormat);
            OutputFileName = FormatPath(OutputFileFormat);
            OutputFile = new FileInfo(Path.GetFullPath(OutputFileName));
            FileName = Path.GetFileNameWithoutExtension(OutputFile.Name);
            _xamlName = null;
        }

    public SearchOption RecursionDepth
        {
            get { return _recursionDepth; }
            set
            {
                _recursionDepth = value;
                _sourceDirectories = null;
            }
        }

        public bool Recurse
        {
            get { return _recurse; }
            set
            {
                _recurse = value;
                _sourceDirectories = null;
            }
        }
        public DirectoryInfo RootDirectory { get; private set; }

        string[] GetSourceDirectories()
        {
            NestedDirectories.Clear();
            if (!Recurse)
            {                
                return new[] {RootDirectory.FullName};
            }
            var directories = RootDirectory.GetDirectories("*", RecursionDepth);
            var sources = new List<string>();
            foreach (var directory in directories)
            {
                var subdirs = directories.Where(x => x.Parent?.FullName.StartsWith(directory.FullName) ?? false).ToArray();
                if (subdirs.Length > 0)
                {
                    var files = Directory.GetFiles(directory.FullName, "*.svg");
                    if (files.Length == 0)
                    {
                        NestedDirectories[directory.FullName] = Directory.GetDirectories(directory.FullName);
                        continue;
                    }
                }
                sources.Add(directory.FullName);
            }
            return sources.ToArray();
        }

        public Dictionary<string, string[]> NestedDirectories { get; } = new Dictionary<string, string[]>();
        public string[] SourceDirectories => _sourceDirectories ?? (_sourceDirectories = GetSourceDirectories());
    }
}