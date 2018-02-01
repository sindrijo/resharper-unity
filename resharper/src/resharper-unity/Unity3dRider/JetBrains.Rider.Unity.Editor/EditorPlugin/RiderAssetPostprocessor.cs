using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JetBrains.Util.Logging;
using UnityEditor;

namespace JetBrains.Rider.Unity.Editor
{
  public class RiderAssetPostprocessor : AssetPostprocessor
  {
    private static readonly ILog ourLogger = Log.GetLog<RiderAssetPostprocessor>();
    
    public static void OnGeneratedCSProjectFiles()
    {
      if (!RiderPlugin.Enabled)
        return;
      var currentDirectory = Directory.GetCurrentDirectory();
      var projectFiles = Directory.GetFiles(currentDirectory, "*.csproj");

      foreach (var file in projectFiles)
      {
        UpgradeProjectFile(file);
      }

      var slnFile = RiderPlugin.SlnFile;
      if (!File.Exists(slnFile))
        return;
      
      ourLogger.Verbose("Post-processing {0}", slnFile);
      var slnAllText = File.ReadAllText(slnFile);
      const string unityProjectGuid = @"Project(""{E097FAD1-6243-4DAD-9C02-E9B9EFC3FFC1}"")";
      if (!slnAllText.Contains(unityProjectGuid))
      {
        var matchGuid = @"Project\(\""\{[A-Z0-9]{8}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{12}\}\""\)";
        // Unity may put a random guid, unityProjectGuid will help VSTU recognize Rider-generated projects
        slnAllText = Regex.Replace(slnAllText, matchGuid, unityProjectGuid);
      }

      var lines = slnAllText.Split(new[] {Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries);
      var sb = new StringBuilder();
      foreach (var line in lines)
      {
        if (line.StartsWith("Project("))
        {
          var mc = Regex.Matches(line, "\"([^\"]*)\"");
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, "mc[1]: "+mc[1].Value);
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, "mc[2]: "+mc[2].Value);
          var to = GetFileNameWithoutExtension(mc[2].Value.Substring(1, mc[2].Value.Length-1)); // remove quotes
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, "to:" + to);
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, line);
          var newLine = line.Substring(0, mc[1].Index + 1) + to + line.Substring(mc[1].Index + mc[1].Value.Length - 1);
          sb.Append(newLine);
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, newLine);
        }
        else
        {
          sb.Append(line);
        }
        sb.Append(Environment.NewLine);
      }
      File.WriteAllText(slnFile, sb.ToString());
    }

    private static string GetFileNameWithoutExtension(string path)
    {
      if (string.IsNullOrEmpty(path))
        return null;
      int length;
      return (length = path.LastIndexOf('.')) == -1 ? path : path.Substring(0, length);
    }

    private static void UpgradeProjectFile(string projectFile)
    {
      ourLogger.Verbose("Post-processing {0}", projectFile);
      XDocument doc;
      try
      {
        doc = XDocument.Load(projectFile);
      }
      catch (Exception)
      {
        ourLogger.Verbose("Failed to Load {0}", projectFile);
        return;
      }
      
      var projectContentElement = doc.Root;
      XNamespace xmlns = projectContentElement.Name.NamespaceName; // do not use var

      FixTargetFrameworkVersion(projectContentElement, xmlns);
      FixSystemXml(projectContentElement, xmlns);
      SetLangVersion(projectContentElement, xmlns);
      
      // Unity_5_6_OR_NEWER switched to nunit 3.5
      if (UnityUtils.UnityVersion >= new Version(5,6))
        ChangeNunitReference(projectContentElement, xmlns);

      
      //#i f !UNITY_2017_1_OR_NEWER // Unity 2017.1 and later has this features by itself
      if (UnityUtils.UnityVersion < new Version(2017, 1))
      {
        SetManuallyDefinedComilingSettings(projectFile, projectContentElement, xmlns);
      }
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Xcode.dll", xmlns, projectContentElement);
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Common.dll", xmlns, projectContentElement);

      ApplyManualCompilingSettingsReferences(projectContentElement, xmlns);
      doc.Save(projectFile);
    }

    private static void FixSystemXml(XElement projectContentElement, XNamespace xmlns)
    {
      var el = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .FirstOrDefault(a => a.Attribute("Include") !=null && a.Attribute("Include").Value=="System.XML");
      if (el != null)
      {
        el.Attribute("Include").Value = "System.Xml";
      }
    }

    private static void ChangeNunitReference(XElement projectContentElement, XNamespace xmlns)
    {
      var el = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .FirstOrDefault(a =>  a.Attribute("Include") !=null && a.Attribute("Include").Value=="nunit.framework");
      if (el != null)
      {
        var hintPath = el.Elements(xmlns + "HintPath").FirstOrDefault();
        if (hintPath != null)
        {
          var path = Path.GetFullPath("Library/resharper-unity-libs/nunit3.5.0/nunit.framework.dll");
          if (new FileInfo(path).Exists)
            hintPath.Value = path;
        }
      }
    }

    private static readonly string  PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.GetFullPath("Assets/mcs.rsp");
    private const string UNITY_PLAYER_PROJECT_NAME = "Assembly-CSharp.csproj";
    private const string UNITY_EDITOR_PROJECT_NAME = "Assembly-CSharp-Editor.csproj";
    private const string UNITY_UNSAFE_KEYWORD = "-unsafe";
    private const string UNITY_DEFINE_KEYWORD = "-define:";
    private static readonly string  PLAYER_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.GetFullPath("Assets/smcs.rsp");
    private static readonly string  EDITOR_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.GetFullPath("Assets/gmcs.rsp");

    private static void SetManuallyDefinedComilingSettings(string projectFile, XElement projectContentElement, XNamespace xmlns)
    {
      string configPath = null;

      if (IsPlayerProjectFile(projectFile) || IsEditorProjectFile(projectFile))
      {
        //Prefer mcs.rsp if it exists
        if (File.Exists(PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH))
        {
          configPath = PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
        }
        else
        {
          if (IsPlayerProjectFile(projectFile))
            configPath = PLAYER_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
          else if (IsEditorProjectFile(projectFile))
            configPath = EDITOR_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;          
        }
      }

      if(!string.IsNullOrEmpty(configPath))
        ApplyManualCompilingSettings(configPath
          , projectContentElement
          , xmlns);
    }

    private static void ApplyManualCompilingSettings(string configFilePath, XElement projectContentElement, XNamespace xmlns)
    {
      if (File.Exists(configFilePath))
      {
        var configText = File.ReadAllText(configFilePath);
        if (configText.Contains(UNITY_UNSAFE_KEYWORD))
        {
          // Add AllowUnsafeBlocks to the .csproj. Unity doesn't generate it (although VSTU does).
          // Strictly necessary to compile unsafe code
          ApplyAllowUnsafeBlocks(projectContentElement, xmlns);
        }
        if (configText.Contains(UNITY_DEFINE_KEYWORD))
        {
          // defines could be
          // 1) -define:DEFINE1,DEFINE2
          // 2) -define:DEFINE1;DEFINE2
          // 3) -define:DEFINE1 -define:DEFINE2
          // 4) -define:DEFINE1,DEFINE2;DEFINE3
          // tested on "-define:DEF1;DEF2 -define:DEF3,DEF4;DEFFFF \n -define:DEF5"
          // result: DEF1, DEF2, DEF3, DEF4, DEFFFF, DEF5

          var definesList = new List<string>();
          var compileFlags = configText.Split(' ', '\n');
          foreach (var flag in compileFlags)
          {
            var f = flag.Trim();
            if (f.Contains(UNITY_DEFINE_KEYWORD))
            {
              var defineEndPos = f.IndexOf(UNITY_DEFINE_KEYWORD) + UNITY_DEFINE_KEYWORD.Length;
              var definesSubString = f.Substring(defineEndPos,f.Length - defineEndPos);
              definesSubString = definesSubString.Replace(";", ",");
              definesList.AddRange(definesSubString.Split(','));
            }
          }

          ApplyCustomDefines(definesList.ToArray(), projectContentElement, xmlns);
        }
      }
    }

    private static void ApplyCustomDefines(string[] customDefines, XElement projectContentElement, XNamespace xmlns)
    {
      var definesString = string.Join(";", customDefines);

      var defineConstants = projectContentElement
        .Elements(xmlns+"PropertyGroup")
        .Elements(xmlns+"DefineConstants")
        .FirstOrDefault(definesConsts=> !string.IsNullOrEmpty(definesConsts.Value));

      defineConstants?.SetValue(defineConstants.Value + ";" + definesString);
    }

    private static void ApplyAllowUnsafeBlocks(XElement projectContentElement, XNamespace xmlns)
    {
      projectContentElement.AddFirst(
        new XElement(xmlns + "PropertyGroup", new XElement(xmlns + "AllowUnsafeBlocks", true)));
    }

    private static bool IsPlayerProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UNITY_PLAYER_PROJECT_NAME;
    }

    private static bool IsEditorProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UNITY_EDITOR_PROJECT_NAME;
    }

    private static void SetXCodeDllReference(string name, XNamespace xmlns, XElement projectContentElement)
    {
      var unityAppBaseFolder = Path.GetDirectoryName(EditorApplication.applicationPath);

      var xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("Data/PlaybackEngines/iOSSupport", name));
      if (!File.Exists(xcodeDllPath))
        xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("PlaybackEngines/iOSSupport", name));

      if (File.Exists(xcodeDllPath))
      {
        var itemGroup = new XElement(xmlns + "ItemGroup");
        var reference = new XElement(xmlns + "Reference");
        reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(xcodeDllPath)));
        reference.Add(new XElement(xmlns + "HintPath", xcodeDllPath));
        itemGroup.Add(reference);
        projectContentElement.Add(itemGroup);
      }
    }

    private const string UNITY_REFERENCE_KEYWORD = "-r:";
    /// <summary>
    /// Handles custom references -r: in "mcs.rsp"
    /// </summary>
    /// <param name="projectContentElement"></param>
    /// <param name="xmlns"></param>
    private static void ApplyManualCompilingSettingsReferences(XElement projectContentElement, XNamespace xmlns)
    {
      if (!File.Exists(PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH))
        return;
      
      var configFilePath = PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;

      if (File.Exists(configFilePath))
      {
        var configText = File.ReadAllText(configFilePath);
        if (configText.Contains(UNITY_REFERENCE_KEYWORD))
        {
          var referenceList = new List<string>();
          var compileFlags = configText.Split(' ', '\n');
          foreach (var flag in compileFlags)
          {
            var f = flag.Trim();
            if (f.Contains(UNITY_REFERENCE_KEYWORD))
            {
              var defineEndPos = f.IndexOf(UNITY_REFERENCE_KEYWORD) + UNITY_REFERENCE_KEYWORD.Length;
              var definesSubString = f.Substring(defineEndPos,f.Length - defineEndPos);
              definesSubString = definesSubString.Replace(";", ",");
              referenceList.AddRange(definesSubString.Split(','));
            }
          }

          foreach (var referenceName in referenceList)
          {
            ApplyCustomReference(referenceName, projectContentElement, xmlns);  
          }
        }
      }
    }

    private static void ApplyCustomReference(string name, XElement projectContentElement, XNamespace xmlns)
    {
      var itemGroup = new XElement(xmlns + "ItemGroup");
      var reference = new XElement(xmlns + "Reference");
      reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(name)));
      itemGroup.Add(reference);
      projectContentElement.Add(itemGroup);
    }

    // Set appropriate version
    private static void FixTargetFrameworkVersion(XElement projectElement, XNamespace xmlns)
    {
      var targetFrameworkVersion = projectElement.Elements(xmlns + "PropertyGroup")
        .Elements(xmlns + "TargetFrameworkVersion")
        .FirstOrDefault(); // Processing csproj files, which are not Unity-generated #56
      if (targetFrameworkVersion != null)
      {
        var scriptingRuntime = 0; // legacy runtime
        try
        {
          var property = typeof(EditorApplication).GetProperty("scriptingRuntimeVersion");
          scriptingRuntime = (int)property.GetValue(null, null);
          if (scriptingRuntime>0)
            ourLogger.Verbose("Latest runtime detected.");
        }
        catch(Exception){}
        
        if (scriptingRuntime>0)
        if (scriptingRuntime>0)
          targetFrameworkVersion.SetValue("v"+PluginSettings.TargetFrameworkVersion);
        else
          targetFrameworkVersion.SetValue("v"+PluginSettings.TargetFrameworkVersionOldMono);
      }
    }

    private static void SetLangVersion(XElement projectElement, XNamespace xmlns)
    {
      // Add LangVersion to the .csproj. Unity doesn't generate it (although VSTU does).
      // Not strictly necessary, as the Unity plugin for Rider will work it out, but setting
      // it makes Rider work if it's not installed.
      var langVersion = projectElement.Elements(xmlns + "PropertyGroup").Elements(xmlns + "LangVersion")
        .FirstOrDefault(); // Processing csproj files, which are not Unity-generated #56
      if (langVersion != null)
      {
        langVersion.SetValue(GetLanguageLevel());
      }
      else
      {
        projectElement.AddFirst(new XElement(xmlns + "PropertyGroup",
          new XElement(xmlns + "LangVersion", GetLanguageLevel())));
      }
    }

    private static string GetLanguageLevel()
    {
      // https://bitbucket.org/alexzzzz/unity-c-5.0-and-6.0-integration/src
      if (Directory.Exists(Path.GetFullPath("CSharp70Support")))
        return "7";
      if (Directory.Exists(Path.GetFullPath("CSharp60Support")))
        return "6";

      var apiCompatibilityLevel = 3;
      // Unity 5.5 supports C# 6, but only when targeting .NET 4.6. The enum doesn't exist pre Unity 5.5
//#i f !UNITY_5_6_OR_NEWER
      //if ((int)PlayerSettings.apiCompatibilityLevel >= 3)
//      #else
//      if ((int) PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup) >= 3)
//#endif

      try
      {
        //PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)
        var method = typeof(PlayerSettings).GetMethod("GetApiCompatibilityLevel");
        var parameter = typeof(EditorUserBuildSettings).GetProperty("selectedBuildTargetGroup");
        var val = parameter.GetValue(null, null);
        apiCompatibilityLevel = (int) method.Invoke(null, new [] {val});
      }
      catch (Exception ex)
      {
        ourLogger.Verbose("Exception on evaluating PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)"+ ex);
      }
      
      try
      {
        var property = typeof(PlayerSettings).GetProperty("apiCompatibilityLevel");
        apiCompatibilityLevel = (int) property.GetValue(null, null);
      }
      catch (Exception)
      {
        ourLogger.Verbose("Exception on evaluating PlayerSettings.apiCompatibilityLevel");
      }

      if (apiCompatibilityLevel >= 3)
        return "6";

      return "4";
    }

    private static Type ourPdb2MdbDriver;
    private static Type Pdb2MdbDriver
    {
      get
      {
        if (ourPdb2MdbDriver != null)
          return ourPdb2MdbDriver;
        Assembly assembly;
        try
        {
          var names = typeof(RiderAssetPostprocessor).Assembly.GetManifestResourceNames();
          foreach (var name in names)
          {
            ourLogger.Verbose(Environment.NewLine+name);  
          }
          
          const string resourceName = "JetBrains.Rider.Unity.Editor.pdb2mdb.exe";
          using (var resourceStream = typeof (RiderAssetPostprocessor).Assembly.GetManifestResourceStream(resourceName))
          {
            using (var memoryStream = new MemoryStream())
            {
              if (resourceStream == null)
              {
                ourLogger.Error("Plugin file not found in manifest resources. " + resourceName);
                return null;
              }
              CopyStream(resourceStream, memoryStream);
              assembly = Assembly.Load(memoryStream.ToArray());
            }    
          }
        }
        catch (Exception)
        {
          ourLogger.Verbose("Loading pdb2mdb failed.");
          assembly = null;
        }

        if (assembly == null)
          return null;
        var type = assembly.GetType("Pdb2Mdb.Driver");
        if (type == null)
          return null;
        return ourPdb2MdbDriver = type;
      }
    }
    
    private static void CopyStream(Stream origin, Stream target)
    {
      var buffer = new byte[8192];
      int count;
      while ((count = origin.Read(buffer, 0, buffer.Length)) > 0)
        target.Write(buffer, 0, count);
    }

    public static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromPath)
    {
      if (!RiderPlugin.Enabled)
        return;
      var toBeConverted = importedAssets.Where(a => 
          a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
          importedAssets.Any(a1 => a1 == Path.ChangeExtension(a, ".pdb")) &&
          importedAssets.All(b => b != Path.ChangeExtension(a, ".dll.mdb")))
        .ToArray();
      foreach (var asset in toBeConverted)
      {
        var pdb = Path.ChangeExtension(asset, ".pdb");
        if (!IsPortablePdb(pdb))
          ConvertSymbolsForAssembly(asset);
        else
          ourLogger.Verbose("mdb generation for Portable pdb is not supported. {0}", pdb);
      }
    }

    private static void ConvertSymbolsForAssembly(string asset)
    {
      if (Pdb2MdbDriver == null)
      {
        ourLogger.Warn("FailedToConvertDebugSymbolsNoPdb2mdb.");
        return;
      }
      
      var method = Pdb2MdbDriver.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
      if (method == null)
      {
        ourLogger.Warn("WarningFailedToConvertDebugSymbolsPdb2mdbMainIsNull.");
        return;
      }

      var strArray = new[] { Path.GetFullPath(asset) };
      method.Invoke(null, new object[] { strArray });
    }
    
    //https://github.com/xamarin/xamarin-android/commit/4e30546f
    private const uint PpdbSignature = 0x424a5342;
    public static bool IsPortablePdb(string filename)
    {
      try
      {
        using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
        {
          using (var br = new BinaryReader(fs))
          {
            return br.ReadUInt32() == PpdbSignature;
          }
        }
      }
      catch
      {
        return false;
      }
    }
  }
}