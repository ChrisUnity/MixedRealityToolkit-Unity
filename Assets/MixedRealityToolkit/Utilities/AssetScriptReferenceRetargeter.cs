﻿#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Microsoft.MixedReality.Toolkit.Build.Editor
{
    internal class ScriptReferenceRetargettingSettings : ScriptableObject
    {
        public const string k_MyCustomSettingsPath = "Assets/Editor/MyCustomSettings.asset";

        [SerializeField]
        private int m_Number;

        [SerializeField]
        private string m_SomeString;

        internal static ScriptReferenceRetargettingSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<ScriptReferenceRetargettingSettings>(k_MyCustomSettingsPath);
            if (settings == null)
            {
                settings = CreateInstance<ScriptReferenceRetargettingSettings>();
                settings.m_Number = 42;
                settings.m_SomeString = "The answer to the universe";
                AssetDatabase.CreateAsset(settings, k_MyCustomSettingsPath);
                AssetDatabase.SaveAssets();
            }
            return settings;
        }

        internal static SerializedObject GetSerializedSettings()
        {
            return new SerializedObject(GetOrCreateSettings());
        }
    }

    internal static class ScriptReferenceRetargettingSettingsIMGUIRegister
    {
        [SettingsProvider]
        public static SettingsProvider CreateMyCustomSettingsProvider()
        {
            SettingsProvider provider = new SettingsProvider("Project/ScriptReferenceRetargetter", SettingsScope.Project)
            {
                // By default the last token of the path is used as display name if no label is provided.
                label = "Script Reference Retargetter Settings",
                // Create the SettingsProvider and initialize its drawing (IMGUI) function in place:
                guiHandler = (searchContext) =>
                {
                    var settings = ScriptReferenceRetargettingSettings.GetSerializedSettings();
                    EditorGUILayout.PropertyField(settings.FindProperty("m_Number"), new GUIContent("My Number"));
                    EditorGUILayout.PropertyField(settings.FindProperty("m_SomeString"), new GUIContent("My String"));
                },

                // Populate the search keywords to enable smart search filtering and label highlighting:
                keywords = new HashSet<string>(new[] { "Number", "Some String" })
            };

            return provider;
        }
    }


    public static class AssetScriptReferenceRetargeter
    {
        private struct ClassInformation
        {
            public string Name;
            public string Namespace;
            public string Guid;
            public long FileId;
        }

        private const string YamlPrefix = "%YAML 1.1";
        private static readonly HashSet<string> ExcludedYamlAssetExtensions = new HashSet<string> { ".jpg", ".csv", ".meta", ".pfx", ".txt", ".nuspec", ".asmdef", ".yml", ".cs", ".md", ".json", ".ttf", ".png", ".shader", ".wav", ".bin", ".gltf", ".glb", ".fbx", ".FBX", ".pdf", ".cginc" };
        private static readonly HashSet<string> ExcludedSuffixFromCopy = new HashSet<string>() { ".cs", ".cs.meta" };

        private const string ScriptFileIdConstant = "11500000";

        [MenuItem("Assets/Retarget To DLL")]
        public static void RetargetAssets()
        {
            try
            {
                RunRetargetToDLL();
                Debug.Log("Complete.");
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed.");
                Debug.LogException(ex);
            }
        }

        private static void RunRetargetToDLL()
        {
            string[] allFilesUnderAssets = Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories);
            //ProcessYAMLAssets(allFilesUnderAssets, Application.dataPath.Replace("Assets", "NuGet/Output"), null);

            Dictionary<string, ClassInformation> scriptFilesReferences = ProcessScripts(allFilesUnderAssets);
            Debug.Log($"Found {scriptFilesReferences.Count} script file references.");

            Dictionary<string, ClassInformation> compiledClassReferences = ProcessCompiledDLLs("PackagedAssemblies", Application.dataPath.Replace("Assets", "NuGet/Plugins"));
            Debug.Log($"Found {compiledClassReferences.Count} compiled class references.");

            Dictionary<string, Tuple<string, long>> remapDictionary = new Dictionary<string, Tuple<string, long>>();

            foreach (var pair in scriptFilesReferences)
            {
                if (compiledClassReferences.TryGetValue(pair.Key, out ClassInformation compiledClassInfo))
                {
                    remapDictionary.Add(pair.Value.Guid, new Tuple<string, long>(compiledClassInfo.Guid, compiledClassInfo.FileId));
                }
                else
                {
                    // Switch to throwing exception later
                    Debug.LogError($"Can't find a compiled version of the script: {pair.Key}; guid: {pair.Value.Guid}");
                }
            }

            ProcessYAMLAssets(allFilesUnderAssets, Application.dataPath.Replace("Assets", "NuGet/Output"), remapDictionary);
        }

        private static void ProcessYAMLAssets(string[] allFilePaths, string outputDirectory, Dictionary<string, Tuple<string, long>> remapDictionary)
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, true);
            }

            HashSet<string> foundNonYamlExtensions = new HashSet<string>();
            List<Tuple<string, string>> yamlAssets = new List<Tuple<string, string>>();
            foreach (string filePath in allFilePaths)
            {
                string targetPath = filePath.Replace(Application.dataPath, outputDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

                if (IsYamlFile(filePath))
                {
                    yamlAssets.Add(new Tuple<string, string>(filePath, targetPath));
                }
                else
                {
                    string extension = Path.GetExtension(filePath);
                    if (!ExcludedYamlAssetExtensions.Contains(extension))
                    {
                        foundNonYamlExtensions.Add(extension);
                    }

                    bool copyFile = true;
                    foreach (var suffix in ExcludedSuffixFromCopy)
                    {
                        if (filePath.EndsWith(suffix))
                        {
                            copyFile = false;
                            break;
                        }
                    }

                    if (copyFile)
                    {
                        File.Copy(filePath, targetPath);
                    }
                }
            }

            foreach (var extension in foundNonYamlExtensions)
            {
                Debug.Log($"Not a YAML extension: {extension}");
            }

            var tasks = yamlAssets.Select(t => Task.Run(() => ProcessYamlFile(t.Item1, t.Item2, remapDictionary)));
            Task.WhenAll(tasks).Wait();
        }

        private static async Task ProcessYamlFile(string filePath, string targetPath, Dictionary<string, Tuple<string, long>> remapDictionary)
        {
            using (StreamReader reader = new StreamReader(filePath))
            using (StreamWriter writer = new StreamWriter(targetPath))
            {
                while (!reader.EndOfStream)
                {
                    string line = await reader.ReadLineAsync();

                    if (line.Contains("m_Script"))
                    {
                        if (!line.Contains('}'))
                        {
                            // Read the second line as well
                            line += await reader.ReadLineAsync();

                            if (!line.Contains('}'))
                            {
                                throw new InvalidDataException($"Unexpected part of YAML line split over more than two lines, starting two lines: {line}");
                            }
                        }

                        if (line.Contains(ScriptFileIdConstant))
                        {
                            Match regexResults = Regex.Match(line, @"guid:\s*([0-9a-fA-F]*)");
                            if (!regexResults.Success || regexResults.Groups.Count != 2 || !regexResults.Groups[1].Success || regexResults.Groups[1].Captures.Count != 1)
                            {
                                throw new InvalidDataException($"Failed to find the guid in line: {line}.");
                            }

                            string guid = regexResults.Groups[1].Captures[0].Value;
                            if (remapDictionary.TryGetValue(guid, out Tuple<string, long> tuple))
                            {
                                line = $"  m_Script: {{fileID: {tuple.Item2}, guid: {tuple.Item1}, type: 3}}";
                            }
                            else
                            {
                                // Switch to error later
                                Debug.LogError($"Couldn't find a script remap for {guid}.");
                            }
                        }
                        // else this is not a script file reference
                    }
                    else if (line.Contains(ScriptFileIdConstant))
                    {
                        throw new InvalidDataException($"Line contains script type but not m_Script: {line}");
                    }
                    //{ fileID: 11500000, guid: 83d9acc7968244a8886f3af591305bcb, type: 3}

                    await writer.WriteLineAsync(line);
                }
            }
        }

        private static bool IsYamlFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (ExcludedYamlAssetExtensions.Contains(extension))
            {
                return false;
            }

            using (StreamReader reader = new StreamReader(filePath))
            {
                return reader.ReadLine().StartsWith(YamlPrefix);
            }
        }

        private static Dictionary<string, ClassInformation> ProcessScripts(string[] allFilePaths)
        {
            int lengthOfPrefix = Application.dataPath.IndexOf("Assets");

            Dictionary<string, ClassInformation> toReturn = new Dictionary<string, ClassInformation>();

            foreach (string filePath in allFilePaths)
            {
                if (Path.GetExtension(filePath) == ".cs")
                {
                    MonoScript monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(filePath.Substring(lengthOfPrefix));
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(monoScript, out string guid, out long fileId))
                    {
                        Type type = monoScript.GetClass();
                        if (type != null)
                        {
                            toReturn.Add(type.FullName, new ClassInformation() { Name = type.Name, Namespace = type.Namespace, FileId = fileId, Guid = guid });
                        }
                        else
                        {
                            Debug.LogWarning($"Found script that we can't get type from: {monoScript.name}");
                        }
                    }
                }
            }

            return toReturn;
        }

        private static Dictionary<string, ClassInformation> ProcessCompiledDLLs(string temporaryDirectoryName, string outputDirectory)
        {
            Assembly[] dlls = CompilationPipeline.GetAssemblies();

            string tmpDirPath = Path.Combine(Application.dataPath, temporaryDirectoryName);
            if (Directory.Exists(tmpDirPath))
            {
                Directory.Delete(tmpDirPath);
            }

            Directory.CreateDirectory(tmpDirPath);

            try
            {
                foreach (Assembly dll in dlls)
                {
                    if (dll.name.Contains("MixedReality"))
                    {
                        File.Copy(dll.outputPath, Path.Combine(tmpDirPath, $"{dll.name}.dll"), true);
                    }
                }

                // Load these directories
                // TODO maybe we don't need to
                AssetDatabase.Refresh();

                Dictionary<string, ClassInformation> toReturn = new Dictionary<string, ClassInformation>();

                if (Directory.Exists(outputDirectory))
                {
                    Directory.Delete(outputDirectory);
                }

                Directory.CreateDirectory(outputDirectory);
                foreach (Assembly dll in dlls)
                {
                    if (dll.name.Contains("MixedReality"))
                    {
                        File.Copy(Path.Combine(tmpDirPath, $"{dll.name}.dll"), Path.Combine(outputDirectory, $"{dll.name}.dll"));
                        File.Copy(Path.Combine(tmpDirPath, $"{dll.name}.dll.meta"), Path.Combine(outputDirectory, $"{dll.name}.dll.meta"));

                        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(Path.Combine("Assets", temporaryDirectoryName, $"{dll.name}.dll"));

                        foreach (Object asset in assets)
                        {
                            MonoScript monoScript = asset as MonoScript;
                            if (!(monoScript is null) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(monoScript, out string guid, out long fileId))
                            {
                                Type type = monoScript.GetClass();
                                toReturn.Add(type.FullName, new ClassInformation() { Name = type.Name, Namespace = type.Namespace, FileId = fileId, Guid = guid });
                            }
                        }
                    }
                }

                return toReturn;
            }
            finally
            {
                Directory.Delete(tmpDirPath, true);
                AssetDatabase.Refresh();
            }
        }
    }
}
#endif