﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Shell;
using Saber.Infrastructure;
using Saber.Infrastructure.Logger;

namespace Saber.Plugin.Program.Programs
{
    [Serializable]
    public class Win32 : IProgram
    {
        public string Name { get; set; }
        public string IcoPath { get; set; }
        public string FullPath { get; set; }
        public string ParentDirectory { get; set; }
        public string ExecutableName { get; set; }
        public string Description { get; set; }
        public bool Valid { get; set; }

        private const string ShortcutExtension = "lnk";
        private const string ApplicationReferenceExtension = "appref-ms";
        private const string ExeExtension = "exe";

        private int Score(string query)
        {
            var score1 = StringMatcher.Score(Name, query);
            var score2 = StringMatcher.ScoreForPinyin(Name, query);
            var score3 = StringMatcher.Score(Description, query);
            var score4 = StringMatcher.ScoreForPinyin(Description, query);
            var score5 = StringMatcher.Score(ExecutableName, query);
            var score = new[] { score1, score2, score3, score4, score5 }.Max();
            return score;
        }


        public Result Result(string query, IPublicAPI api)
        {
            var result = new Result
            {
                SubTitle = FullPath,
                IcoPath = IcoPath,
                Score = Score(query),
                ContextData = this,
                Action = e =>
                {
                    var info = new ProcessStartInfo
                    {
                        FileName = FullPath,
                        WorkingDirectory = ParentDirectory
                    };
                    var hide = Main.StartProcess(info);
                    return hide;
                }
            };

            if (Description.Length >= Name.Length &&
                Description.Substring(0, Name.Length) == Name)
            {
                result.Title = Description;
            }
            else if (!string.IsNullOrEmpty(Description))
            {
                result.Title = $"{Name}: {Description}";
            }
            else
            {
                result.Title = Name;
            }

            return result;
        }


        public List<Result> ContextMenus(IPublicAPI api)
        {
            var contextMenus = new List<Result>
            {
                new Result
                {
                    Title = "以管理员身份运行",
                    Action = _ =>
                    {
                        var info = new ProcessStartInfo
                        {
                            FileName = FullPath,
                            WorkingDirectory = ParentDirectory,
                            Verb = "runas"
                        };
                        var hide = Main.StartProcess(info);
                        return hide;
                    },
                    IcoPath = "Images/cmd.png"
                },
                new Result
                {
                    Title = "打开所属文件夹",
                    Action = _ =>
                    {
                        var hide = Main.StartProcess(new ProcessStartInfo(ParentDirectory));
                        return hide;
                    },
                    IcoPath = "Images/folder.png"
                }
            };
            return contextMenus;
        }



        public override string ToString()
        {
            return ExecutableName;
        }

        private static Win32 Win32Program(string path)
        {
            var p = new Win32
            {
                Name = Path.GetFileNameWithoutExtension(path),
                IcoPath = path,
                FullPath = path,
                ParentDirectory = Directory.GetParent(path).FullName,
                Description = string.Empty,
                Valid = true
            };
            return p;
        }

        private static Win32 LnkProgram(string path)
        {
            var program = Win32Program(path);
            try
            {
                var link = new ShellLink();
                const uint STGM_READ = 0;
                ((IPersistFile)link).Load(path, STGM_READ);
                var hwnd = new _RemotableHandle();
                link.Resolve(ref hwnd, 0);

                const int MAX_PATH = 260;
                StringBuilder buffer = new StringBuilder(MAX_PATH);

                var data = new _WIN32_FIND_DATAW();
                const uint SLGP_SHORTPATH = 1;
                link.GetPath(buffer, buffer.Capacity, ref data, SLGP_SHORTPATH);
                var target = buffer.ToString();
                if (!string.IsNullOrEmpty(target))
                {
                    var extension = Extension(target);
                    if (extension == ExeExtension && File.Exists(target))
                    {
                        buffer = new StringBuilder(MAX_PATH);
                        link.GetDescription(buffer, MAX_PATH);
                        var description = buffer.ToString();
                        if (!string.IsNullOrEmpty(description))
                        {
                            program.Description = description;
                        }
                        else
                        {
                            var info = FileVersionInfo.GetVersionInfo(target);
                            if (!string.IsNullOrEmpty(info.FileDescription))
                            {
                                program.Description = info.FileDescription;
                            }
                        }
                    }
                }
                return program;
            }
            catch (COMException e)
            {
                // C:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\MiracastView.lnk always cause exception
                Log.Exception($"|Win32.LnkProgram|COMException when parsing shortcut <{path}> with HResult <{e.HResult}>", e);
                program.Valid = false;
                return program;
            }
            catch (Exception e)
            {
                Log.Exception($"|Win32.LnkProgram|Exception when parsing shortcut <{path}>", e);
                program.Valid = false;
                return program;
            }
        }

        private static Win32 ExeProgram(string path)
        {
            var program = Win32Program(path);
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrEmpty(info.FileDescription))
            {
                program.Description = info.FileDescription;
            }
            return program;
        }

        private static IEnumerable<string> ProgramPaths(string directory, string[] suffixes, Indexing _indexing = null)
        {
            if (!Directory.Exists(directory))
                return new string[] { };

            var ds = Directory.GetDirectories(directory);

            var paths = ds.SelectMany(d =>
            {

                if (d.IndexOf("$RECYCLE.BIN") >= 0 || d.IndexOf("System Volume Information") >= 0)
                {
                    return new List<string>();
                }

                //try
                //{
                var paths_for_suffixes = suffixes.SelectMany(s =>
                {
                    try
                    {
                        var pattern = $"*.{s}";
                        var ps = Directory.EnumerateFiles(d, pattern, SearchOption.AllDirectories);
                        //这里测试下， 否则外面的 抛异常 这里才会抛 UnauthorizedAccessException
                        //ps.ToArray();
                        return ps;
                    }
                    catch (Exception e) when (e is SecurityException || e is UnauthorizedAccessException)
                    {
                        _indexing.Text = string.Format("索引{0}遇到权限问题", d + " " + s);
                        Log.Exception($"|Program.Win32.ProgramPaths|Don't have permission on <{d}> <{s}>", e);
                        return new List<string>();
                    }
                });
                try
                {
                    int size = paths_for_suffixes.ToArray().Length;

                    if (null != _indexing)
                    {
                        _indexing.Text = string.Format("完成{0}索引", d + "(" + size + ")");
                    }
                }
                catch (Exception e) when (e is SecurityException || e is UnauthorizedAccessException)
                {
                    _indexing.Text = string.Format("索引{0}遇到权限问题", d);
                    _indexing.Text = e.Message;
                    Log.Exception($"|Program.Win32.ProgramPaths|Don't have permission on <{d}>", e);
                    return new List<string>();
                }

                return paths_for_suffixes;
                //}
                //catch (Exception e) when (e is SecurityException || e is UnauthorizedAccessException)
                //{
                //    Log.Exception($"|Program.Win32.ProgramPaths|Don't have permission on <{d}>", e);
                //    return new List<string>();
                //}
            });
            return paths;
        }

        private static string Extension(string path)
        {
            var extension = Path.GetExtension(path)?.ToLower();
            if (!string.IsNullOrEmpty(extension))
            {
                return extension.Substring(1);
            }
            else
            {
                return string.Empty;
            }
        }

        public static ParallelQuery<Win32> UnregisteredPrograms(List<Settings.ProgramSource> sources, string[] suffixes, Indexing _indexing)
        {
            var paths = sources.Where(s => Directory.Exists(s.Location))
                               .SelectMany(s => ProgramPaths(s.Location, suffixes, _indexing))
                               .ToArray();
            var programs1 = paths.AsParallel().Where(p => Extension(p) == ExeExtension).Select(ExeProgram);
            var programs2 = paths.AsParallel().Where(p => Extension(p) == ShortcutExtension).Select(ExeProgram);
            var programs3 = from p in paths.AsParallel()
                            let e = Extension(p)
                            where e != ShortcutExtension && e != ExeExtension
                            select Win32Program(p);
            return programs1.Concat(programs2).Concat(programs3);
        }

        public static ParallelQuery<Win32> StartMenuPrograms(string[] suffixes)
        {
            var directory1 = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var directory2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            var paths1 = ProgramPaths(directory1, suffixes);
            var paths2 = ProgramPaths(directory2, suffixes);
            var paths = paths1.Concat(paths2).ToArray();
            var programs1 = paths.AsParallel().Where(p => Extension(p) == ShortcutExtension).Select(LnkProgram);
            var programs2 = paths.AsParallel().Where(p => Extension(p) == ApplicationReferenceExtension).Select(Win32Program);
            var programs = programs1.Concat(programs2).Where(p => p.Valid);
            return programs;
        }


        public static ParallelQuery<Win32> AppPathsPrograms(string[] suffixes)
        {
            // https://msdn.microsoft.com/en-us/library/windows/desktop/ee872121
            const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
            var programs = new List<Win32>();
            using (var root = Registry.LocalMachine.OpenSubKey(appPaths))
            {
                if (root != null)
                {
                    programs.AddRange(ProgramsFromRegistryKey(root));
                }
            }
            using (var root = Registry.CurrentUser.OpenSubKey(appPaths))
            {
                if (root != null)
                {
                    programs.AddRange(ProgramsFromRegistryKey(root));
                }
            }
            var filtered = programs.AsParallel().Where(p => suffixes.Contains(Extension(p.ExecutableName)));
            return filtered;
        }

        private static IEnumerable<Win32> ProgramsFromRegistryKey(RegistryKey root)
        {
            var programs = root.GetSubKeyNames()
                               .Select(subkey => ProgramFromRegistrySubkey(root, subkey))
                               .Where(p => !string.IsNullOrEmpty(p.Name));
            return programs;
        }

        private static Win32 ProgramFromRegistrySubkey(RegistryKey root, string subkey)
        {
            using (var key = root.OpenSubKey(subkey))
            {
                if (key != null)
                {
                    var defaultValue = string.Empty;
                    var path = key.GetValue(defaultValue) as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        // fix path like this: ""\"C:\\folder\\executable.exe\""
                        path = path.Trim('"', ' ');
                        path = Environment.ExpandEnvironmentVariables(path);

                        if (File.Exists(path))
                        {
                            var entry = Win32Program(path);
                            entry.ExecutableName = subkey;
                            return entry;
                        }
                        else
                        {
                            return new Win32();
                        }
                    }
                    else
                    {
                        return new Win32();
                    }
                }
                else
                {
                    return new Win32();
                }
            }
        }

        //private static Win32 ScoreFilter(Win32 p)
        //{
        //    var start = new[] { "启动", "start" };
        //    var doc = new[] { "帮助", "help", "文档", "documentation" };
        //    var uninstall = new[] { "卸载", "uninstall" };

        //    var contained = start.Any(s => p.Name.ToLower().Contains(s));
        //    if (contained)
        //    {
        //        p.Score += 10;
        //    }
        //    contained = doc.Any(d => p.Name.ToLower().Contains(d));
        //    if (contained)
        //    {
        //        p.Score -= 10;
        //    }
        //    contained = uninstall.Any(u => p.Name.ToLower().Contains(u));
        //    if (contained)
        //    {
        //        p.Score -= 20;
        //    }

        //    return p;
        //}

        //public static Win32[] All(Settings settings)
        //{
        //    ParallelQuery<Win32> programs = new List<Win32>().AsParallel();
        //    if (settings.EnableRegistrySource)
        //    {
        //        var appPaths = AppPathsPrograms(settings.ProgramSuffixes);
        //        programs = programs.Concat(appPaths);
        //    }
        //    if (settings.EnableStartMenuSource)
        //    {
        //        var startMenu = StartMenuPrograms(settings.ProgramSuffixes);
        //        programs = programs.Concat(startMenu);
        //    }
        //    var unregistered = UnregisteredPrograms(settings.ProgramSources, settings.ProgramSuffixes);
        //    programs = programs.Concat(unregistered);
        //    //.Select(ScoreFilter);
        //    return programs.ToArray();
        //}
    }
}
