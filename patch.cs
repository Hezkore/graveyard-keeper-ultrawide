// Patches Graveyard Keeper so it runs at any monitor resolution
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Mono.Cecil;
using Mono.Cecil.Cil;


// Holds everything the patcher does, kept as one class on purpose
class Patcher
{
	// = Configuration ============================================================
	const string BACKUP_SUFFIX = ".bak-original";

	// Original caps inside the DLL, raised so no monitor ever hits them
	const int ORIGINAL_CAP_WIDTH  = 2560;
	const int ORIGINAL_CAP_HEIGHT = 1440;
	const int NEW_CAP_WIDTH       = 16_384;
	const int NEW_CAP_HEIGHT      = 8_192;

	// Fallback resolution the game uses when nothing is saved yet
	const int ORIGINAL_FALLBACK_WIDTH  = 1920;
	const int ORIGINAL_FALLBACK_HEIGHT = 1080;

	// Fog is a tiled grid of sprites, each tile is 576 pixels wide
	// Original 6 and 36 get replaced by a formula based on screen width
	const int   FOG_TILE_WIDTH_PIXELS = 576;
	const int   FOG_BASE_GRID         = 6;
	const int   FOG_EXTRA_COLUMNS     = 2;
	const float FOG_WRAP_DISTANCE     = 36f;

	// Cursor mode values from the game enum, 0 default, 1 hardware, 2 software
	const int CURSOR_HARDWARE = 1;
	const int CURSOR_SOFTWARE = 2;

	// Steam app id for Graveyard Keeper, used to find the Proton prefix
	const int STEAM_APP_ID = 599_140;

	// Offset from the level2 anchor start to the m_Enabled flag
	const int LEVEL2_ENABLED_OFFSET = 12;


	// = Setup ====================================================================
	static Patcher()
	{
		// Mono.Cecil lives inside this exe as a resource, hand it back when asked
		AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
		{
			var wanted = new System.Reflection.AssemblyName(args.Name).Name;
			if (wanted != "Mono.Cecil") return null;
			var self = System.Reflection.Assembly.GetExecutingAssembly();
			using (var stream = self.GetManifestResourceStream("Mono.Cecil.dll"))
			{
				if (stream == null) return null;
				var bytes = new byte[stream.Length];
				stream.Read(bytes, 0, bytes.Length);
				return System.Reflection.Assembly.Load(bytes);
			}
		};
	}


	// = Entry point ==============================================================
	static int Main(string[] args)
	{
		PrintUsage();
		try
		{
			string managedDir    = FindManagedDir(args.Length > 0 ? args[0] : null);
			string dllPath       = Path.Combine(managedDir, "Assembly-CSharp.dll");
			string dllBackup     = dllPath + BACKUP_SUFFIX;
			string dataDir       = Path.GetDirectoryName(managedDir);
			string gameRoot      = Path.GetDirectoryName(dataDir);
			string level2Path    = Path.Combine(dataDir, "level2");
			string level2Backup  = level2Path + BACKUP_SUFFIX;

			if (!File.Exists(dllPath))
			{
				throw new InvalidOperationException(
					"No Assembly-CSharp.dll in " + managedDir);
			}

			string build;
			string store;
			DetectBuild(gameRoot, dataDir, out build, out store);
			string version = ReadGameVersion(dllPath);

			PrintSummary(build, store, version, dllPath, dllBackup,
				level2Path, level2Backup);

			PrintPlan();

			if (!File.Exists(dllBackup))
			{
				File.Copy(dllPath, dllBackup);
				Console.WriteLine("Made backup at " + dllBackup);
			}

			var resolver = new DefaultAssemblyResolver();
			resolver.AddSearchDirectory(managedDir);
			var readerParams = new ReaderParameters { AssemblyResolver = resolver };

			// Read from the backup so running twice doesn't stack patches
			var asm    = AssemblyDefinition.ReadAssembly(dllBackup, readerParams);
			var module = asm.MainModule;

			var resolutionConfig = module.GetType("ResolutionConfig");
			var gameSettings     = module.GetType("GameSettings");
			var platformSpecific = module.GetType("PlatformSpecific");

			PromoteShortBranches(resolutionConfig, gameSettings, platformSpecific);

			PatchResolutionCap(resolutionConfig);
			PatchIsHardwareSupported(resolutionConfig);

			var unity = LoadUnityReferences(module, resolver);

			PatchApplyScreenMode(gameSettings, unity);
			PatchInitResolutions(resolutionConfig, module, unity);
			PatchFogObject(module, unity);
			PatchCursorDefault(gameSettings, platformSpecific);

			// screen_mode default is already 0 which is Borderless, nothing to do

			asm.Write(dllPath);
			Console.WriteLine("  Wrote " + dllPath);

			var level2Outcome = PatchLevel2(level2Path, level2Backup);
			ClearSavedSettings();

			PrintFinalStatus(level2Outcome);
			return 0;
		}
		catch (InvalidOperationException error)  { return ReportError(error); }
		catch (IOException error)                { return ReportError(error); }
		catch (UnauthorizedAccessException error) { return ReportError(error); }
		catch (BadImageFormatException error)    { return ReportError(error); }
	}


	// = Console output ===========================================================
	static void PrintUsage()
	{
		Console.WriteLine("Graveyard Keeper resolution ultrawide patcher");
		Console.WriteLine();
		Console.WriteLine("This is a .NET exe, runs the same anywhere");
		Console.WriteLine("  Windows, double click patch.exe or run it in a terminal");
		Console.WriteLine("  Linux,   mono patch.exe   (need mono installed)");
		Console.WriteLine("  macOS,   mono patch.exe   (need mono installed, eg brew install mono)");
		Console.WriteLine();
		Console.WriteLine("Usage, patch.exe [path to Graveyard Keeper install]");
		Console.WriteLine("If no path is given I look in the current folder,");
		Console.WriteLine("the folder the exe is in, and the usual Steam and GOG spots");
		Console.WriteLine();
	}

	static void PrintSummary(string build, string store, string version,
		string dllPath, string dllBackup,
		string level2Path, string level2Backup)
	{
		Console.WriteLine("What I found");
		Console.WriteLine("  build    , " + build);
		Console.WriteLine("  store    , " + store);
		Console.WriteLine("  version  , " + version);
		Console.WriteLine("  dll size , "
			+ new FileInfo(dllPath).Length.ToString("N0") + " bytes");
		Console.WriteLine();

		Console.WriteLine("Files I'm going to touch");
		Console.WriteLine("  dll      , " + dllPath);
		Console.WriteLine("             (backup at " + dllBackup + ")");
		Console.WriteLine("  level2   , " + level2Path
			+ (File.Exists(level2Path)
				? ""
				: "  (missing, I'll skip the small_font fix)"));
		if (File.Exists(level2Path))
			Console.WriteLine("             (backup at " + level2Backup + ")");
		Console.WriteLine();

		Console.WriteLine("Saved settings I'll delete if I find them");
		foreach (var path in SettingsPaths())
		{
			string prefix = File.Exists(path) ? "there , " : "empty , ";
			Console.WriteLine("  " + prefix + path);
		}
		Console.WriteLine();
	}

	static void PrintPlan()
	{
		Console.WriteLine("What I'm about to do");
		Console.WriteLine("  First kill the 2560x1440 cap");
		Console.WriteLine("  Make IsHardwareSupported always say yes");
		Console.WriteLine("  When no resolution is saved, pick the monitors native one");
		Console.WriteLine("  Add the monitors native resolution to the in game list");
		Console.WriteLine("  Make fog cover the whole screen width");
		Console.WriteLine("  Default the cursor to Hardware not Software");
		Console.WriteLine("  Hide the small_font debug label on the main menu");
		Console.WriteLine("    inside level2 the main menu scene file");
		Console.WriteLine("  Delete any saved settings so the new defaults kick in");
		Console.WriteLine();
	}

	static void PrintFinalStatus(Level2Result level2Outcome)
	{
		Console.WriteLine();
		if (level2Outcome == Level2Result.Patched
			|| level2Outcome == Level2Result.AlreadyPatched)
		{
			Console.WriteLine(
				"All done, dll patched, level2 patched, saved settings cleared");
		}
		else
		{
			Console.WriteLine("Dll patched and saved settings cleared");
			Console.WriteLine(
				"Couldn't disable the main menu small_font label, see above");
			Console.WriteLine(
				"That label may still show up on ultrawides, everything else is fine");
		}
		Console.WriteLine(
			"Launch the game, it should open at my monitors native resolution");
		Console.WriteLine("borderless with a hardware cursor");
	}

	static int ReportError(Exception error)
	{
		Console.WriteLine();
		Console.WriteLine("Something went wrong, " + error.Message);
		Console.WriteLine();
		Console.WriteLine("Full details");
		Console.WriteLine(error);
		return 1;
	}


	// = Install discovery ========================================================
	// Try user arg, cwd, exe folder, then known Steam or GOG paths
	// First folder with Assembly-CSharp.dll wins, else throw with the list
	static string FindManagedDir(string userArg)
	{
		var searchPaths = new List<string>();
		if (!string.IsNullOrEmpty(userArg))
			searchPaths.AddRange(ManagedSubpaths(userArg));
		searchPaths.AddRange(ManagedSubpaths(Directory.GetCurrentDirectory()));

		var exeDir = Path.GetDirectoryName(
			System.Reflection.Assembly.GetEntryAssembly().Location);
		if (!string.IsNullOrEmpty(exeDir))
			searchPaths.AddRange(ManagedSubpaths(exeDir));

		foreach (var path in DefaultInstallPaths())
			searchPaths.AddRange(ManagedSubpaths(path));

		foreach (var path in searchPaths.Distinct())
		{
			if (Directory.Exists(path)
				&& File.Exists(Path.Combine(path, "Assembly-CSharp.dll")))
			{
				return path;
			}
		}

		var message = new StringBuilder();
		message.AppendLine("I can't find a folder with Assembly-CSharp.dll");
		message.AppendLine("Pass the game install path as the first argument, for example");
		message.AppendLine(
			"  mono patch.exe \"/home/me/.local/share/Steam/steamapps/common/Graveyard Keeper\"");
		message.AppendLine(
			"  patch.exe \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Graveyard Keeper\"");
		message.AppendLine();
		message.AppendLine("I looked in");
		foreach (var path in searchPaths.Distinct())
			message.AppendLine("  " + path);
		throw new InvalidOperationException(message.ToString());
	}

	// For a folder, list every sub path where the DLL can live
	static IEnumerable<string> ManagedSubpaths(string folder)
	{
		if (string.IsNullOrEmpty(folder)) yield break;
		yield return folder;
		yield return Path.Combine(folder, "Managed");
		yield return Path.Combine(folder, "Graveyard Keeper_Data", "Managed");
		yield return Path.Combine(folder, "Contents", "Resources", "Data", "Managed");
		yield return Path.Combine(folder,
			"Graveyard Keeper.app", "Contents", "Resources", "Data", "Managed");
	}

	static IEnumerable<string> DefaultInstallPaths()
	{
		string home = GetHomeFolder();
		yield return Path.Combine(home,
			".local/share/Steam/steamapps/common/Graveyard Keeper");
		yield return Path.Combine(home,
			"Library/Application Support/Steam/steamapps/common/Graveyard Keeper");
		yield return @"C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper";
		yield return @"C:\Program Files\Steam\steamapps\common\Graveyard Keeper";
		yield return @"C:\Program Files (x86)\GOG Galaxy\Games\Graveyard Keeper";
		yield return @"C:\GOG Games\Graveyard Keeper";
		yield return Path.Combine(home, "GOG Games", "Graveyard Keeper");
	}

	static string GetHomeFolder()
	{
		return Environment.GetEnvironmentVariable("HOME")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	}

	// Guess the OS and store from what files are around
	static void DetectBuild(string gameRoot, string dataDir,
		out string build, out string store)
	{
		build = "unknown";
		store = "unknown";

		if (gameRoot != null && Directory.Exists(gameRoot))
		{
			bool hasWinExe = File.Exists(Path.Combine(gameRoot, "Graveyard Keeper.exe"))
				|| File.Exists(Path.Combine(gameRoot, "UnityPlayer.dll"));
			bool hasLinuxExe = File.Exists(Path.Combine(gameRoot, "Graveyard Keeper.x86_64"))
				|| File.Exists(Path.Combine(gameRoot, "Graveyard Keeper.x86"));
			bool looksLikeMac = gameRoot.EndsWith(".app")
				|| Directory.Exists(Path.Combine(gameRoot, "Contents", "MacOS"));

			if (looksLikeMac)     build = "Mac (Unity Mono)";
			else if (hasWinExe)   build = "Windows (Unity Mono)";
			else if (hasLinuxExe) build = "Linux (Unity Mono)";
		}

		bool inSteamFolder = gameRoot != null
			&& gameRoot.Replace('\\', '/').Contains("/steamapps/common/");
		bool hasSteamApi = dataDir != null && (
			File.Exists(Path.Combine(dataDir, "Plugins", "libsteam_api.so")) ||
			File.Exists(Path.Combine(dataDir, "Plugins", "steam_api.dll")) ||
			File.Exists(Path.Combine(dataDir, "Plugins", "steam_api64.dll")) ||
			File.Exists(Path.Combine(dataDir, "Plugins", "x86_64", "steam_api.dll")) ||
			File.Exists(Path.Combine(dataDir, "Plugins", "x86_64", "steam_api64.dll")));
		bool looksLikeGog = gameRoot != null
			&& Directory.EnumerateFiles(gameRoot, "goggame-*.info").Any();

		if (looksLikeGog)                      store = "GOG";
		else if (inSteamFolder || hasSteamApi) store = "Steam";

		// Windows build in a Steam folder on Linux means Proton
		if (inSteamFolder && build.StartsWith("Windows")
			&& Environment.OSVersion.Platform == PlatformID.Unix)
		{
			store += " (via Proton)";
		}
	}

	// Read LazyConsts.VERSION_INT and format as 1.407
	static string ReadGameVersion(string dllPath)
	{
		try
		{
			// InMemory so the file handle isn't held open
			var asm = AssemblyDefinition.ReadAssembly(dllPath,
				new ReaderParameters { InMemory = true });
			var lazyConsts = asm.MainModule.GetType("LazyConsts");
			if (lazyConsts == null) return "unknown (no LazyConsts class)";

			var field = lazyConsts.Fields.FirstOrDefault(f => f.Name == "VERSION_INT");
			if (field != null && field.HasConstant)
			{
				float raw = Convert.ToSingle(field.Constant);
				return (raw / 1000f).ToString("0.000",
					CultureInfo.InvariantCulture);
			}

			// Older builds store the version as an inline float inside a getter
			var getter = lazyConsts.Methods
				.FirstOrDefault(m => m.Name == "get_VERSION");
			if (getter != null && getter.HasBody)
			{
				foreach (var instruction in getter.Body.Instructions)
				{
					if (instruction.OpCode == OpCodes.Ldc_R4)
					{
						return Convert.ToSingle(instruction.Operand)
							.ToString("0.000", CultureInfo.InvariantCulture);
					}
				}
			}
		}
		catch (IOException)             { /* can't read, fall through */ }
		catch (BadImageFormatException) { /* not a managed DLL */ }
		return "unknown";
	}


	// = Patch helpers ============================================================
	// Unity references the patches need, kept together to pass around
	class UnityRefs
	{
		public TypeReference   Resolution;
		public MethodReference ScreenCurrent;
		public MethodReference ResolutionWidth;
		public MethodReference ResolutionHeight;
		public MethodReference ScreenWidth;
		public MethodReference MathfCeilToInt;
		public MethodReference MathMax;
	}

	static UnityRefs LoadUnityReferences(ModuleDefinition module,
		DefaultAssemblyResolver resolver)
	{
		var unityCoreRef = module.AssemblyReferences
			.First(r => r.Name == "UnityEngine.CoreModule");
		var unityCore = resolver.Resolve(unityCoreRef).MainModule;
		var resolutionType = unityCore.GetType("UnityEngine.Resolution");
		var screenType     = unityCore.GetType("UnityEngine.Screen");
		var mathfType      = unityCore.GetType("UnityEngine.Mathf");
		var mathType       = module.TypeSystem.Int32.Resolve().Module
			.GetType("System.Math");

		var refs = new UnityRefs();
		refs.Resolution       = module.ImportReference(resolutionType);
		refs.ScreenCurrent    = module.ImportReference(
			screenType.Methods.First(m => m.Name == "get_currentResolution"));
		refs.ResolutionWidth  = module.ImportReference(
			resolutionType.Methods.First(m => m.Name == "get_width"));
		refs.ResolutionHeight = module.ImportReference(
			resolutionType.Methods.First(m => m.Name == "get_height"));
		refs.ScreenWidth      = module.ImportReference(
			screenType.Methods.First(m => m.Name == "get_width"));
		refs.MathfCeilToInt   = module.ImportReference(
			mathfType.Methods.First(m =>
				m.Name == "CeilToInt" && m.Parameters.Count == 1
				&& m.Parameters[0].ParameterType.FullName == "System.Single"));
		refs.MathMax          = module.ImportReference(
			mathType.Methods.First(m =>
				m.Name == "Max" && m.Parameters.Count == 2
				&& m.Parameters[0].ParameterType.FullName == "System.Int32"));
		return refs;
	}

	// Short branches only reach a 1 byte offset, make them long before editing
	// so newly inserted instructions don't push the target out of reach
	static void PromoteShortBranches(TypeDefinition resolutionConfig,
		TypeDefinition gameSettings, TypeDefinition platformSpecific)
	{
		var methods = new[] {
			resolutionConfig.Methods.First(m => m.Name == "GetResolutionConfigOrNull"),
			resolutionConfig.Methods.First(m => m.Name == "IsHardwareSupported"),
			resolutionConfig.Methods.First(m => m.Name == "InitResolutions"),
			gameSettings.Methods.First(m => m.Name == "ApplyScreenMode"),
			gameSettings.Methods.First(m => m.IsConstructor && !m.IsStatic),
			platformSpecific.Methods.First(m => m.Name == "LoadGameSettings"),
		};
		foreach (var method in methods)
			CecilHelpers.PromoteShortBranches(method.Body);
	}

	// Swap the hardcoded 1440 and 2560 for values nothing will hit
	static void PatchResolutionCap(TypeDefinition resolutionConfig)
	{
		var method = resolutionConfig.Methods
			.First(m => m.Name == "GetResolutionConfigOrNull");
		foreach (var instruction in method.Body.Instructions.ToList())
		{
			if (instruction.OpCode == OpCodes.Ldc_I4 && instruction.Operand is int value)
			{
				if (value == ORIGINAL_CAP_HEIGHT)     instruction.Operand = NEW_CAP_HEIGHT;
				else if (value == ORIGINAL_CAP_WIDTH) instruction.Operand = NEW_CAP_WIDTH;
			}
		}
		Console.WriteLine("  Ok killed resolution caps");
	}

	// Replace the whole method with a single return true
	static void PatchIsHardwareSupported(TypeDefinition resolutionConfig)
	{
		var method = resolutionConfig.Methods
			.First(m => m.Name == "IsHardwareSupported");
		method.Body.Instructions.Clear();
		method.Body.ExceptionHandlers.Clear();
		method.Body.Variables.Clear();
		var il = method.Body.GetILProcessor();
		il.Append(il.Create(OpCodes.Ldc_I4_1));
		il.Append(il.Create(OpCodes.Ret));
		Console.WriteLine("  Ok IsHardwareSupported always true");
	}

	// Swap the 1920 and 1080 fallback for Screen.currentResolution width and height
	static void PatchApplyScreenMode(TypeDefinition gameSettings, UnityRefs unity)
	{
		var method = gameSettings.Methods.First(m => m.Name == "ApplyScreenMode");
		var body = method.Body;
		var il = body.GetILProcessor();

		var resolutionLocal = new VariableDefinition(unity.Resolution);
		body.Variables.Add(resolutionLocal);
		body.InitLocals = true;

		Instruction widthInstruction  = null;
		Instruction heightInstruction = null;
		foreach (var instruction in body.Instructions)
		{
			if (instruction.OpCode == OpCodes.Ldc_I4 && instruction.Operand is int value)
			{
				if (value == ORIGINAL_FALLBACK_WIDTH)       widthInstruction  = instruction;
				else if (value == ORIGINAL_FALLBACK_HEIGHT) heightInstruction = instruction;
			}
		}
		if (widthInstruction == null || heightInstruction == null)
		{
			throw new InvalidOperationException(
				"Didn't find 1920 or 1080 in ApplyScreenMode");
		}

		// Replace the 1920 push with get currentResolution then read width
		var callGetResolution = il.Create(OpCodes.Call, unity.ScreenCurrent);
		var storeResolution   = il.Create(OpCodes.Stloc, resolutionLocal);
		var loadResolutionRef = il.Create(OpCodes.Ldloca, resolutionLocal);
		var callGetWidth      = il.Create(OpCodes.Call, unity.ResolutionWidth);
		il.Replace(widthInstruction, callGetResolution);
		il.InsertAfter(callGetResolution, storeResolution);
		il.InsertAfter(storeResolution, loadResolutionRef);
		il.InsertAfter(loadResolutionRef, callGetWidth);

		// Replace the 1080 push with the same struct reread for height
		var loadResolutionRefAgain = il.Create(OpCodes.Ldloca, resolutionLocal);
		var callGetHeight          = il.Create(OpCodes.Call, unity.ResolutionHeight);
		il.Replace(heightInstruction, loadResolutionRefAgain);
		il.InsertAfter(loadResolutionRefAgain, callGetHeight);

		Console.WriteLine("  Ok ApplyScreenMode defaults to the native resolution");
	}

	// Unity skips the native resolution sometimes, so append it if it isn't listed
	static void PatchInitResolutions(TypeDefinition resolutionConfig,
		ModuleDefinition module, UnityRefs unity)
	{
		var method = resolutionConfig.Methods
			.First(m => m.Name == "InitResolutions");
		var body = method.Body;
		var il = body.GetILProcessor();
		body.InitLocals = true;

		var availableField = resolutionConfig.Fields
			.First(f => f.Name == "_available_resolutions");
		var xField = resolutionConfig.Fields.First(f => f.Name == "x");
		var yField = resolutionConfig.Fields.First(f => f.Name == "y");
		var resolutionCtor = resolutionConfig.Methods
			.First(m => m.IsConstructor && m.Parameters.Count == 2);

		var listType = (GenericInstanceType)availableField.FieldType;
		var listGenericArgs = listType.GenericArguments.ToArray();
		var listAdd = module.ImportReference(
			listType.Resolve().Methods.First(m => m.Name == "Add"))
			.MakeHostInstanceGeneric(listGenericArgs);
		var listCount = module.ImportReference(
			listType.Resolve().Methods.First(m => m.Name == "get_Count"))
			.MakeHostInstanceGeneric(listGenericArgs);
		var listGetItem = module.ImportReference(
			listType.Resolve().Methods.First(m => m.Name == "get_Item"))
			.MakeHostInstanceGeneric(listGenericArgs);

		var currentResolution = new VariableDefinition(unity.Resolution);
		var monitorWidth      = new VariableDefinition(module.TypeSystem.Int32);
		var monitorHeight     = new VariableDefinition(module.TypeSystem.Int32);
		var alreadyInList     = new VariableDefinition(module.TypeSystem.Boolean);
		var loopIndex         = new VariableDefinition(module.TypeSystem.Int32);
		var listItem          = new VariableDefinition(resolutionConfig);
		body.Variables.Add(currentResolution);
		body.Variables.Add(monitorWidth);
		body.Variables.Add(monitorHeight);
		body.Variables.Add(alreadyInList);
		body.Variables.Add(loopIndex);
		body.Variables.Add(listItem);

		// Inject right before the end of method count check
		Instruction insertBefore = null;
		for (int i = 0; i < body.Instructions.Count - 1; i++)
		{
			var first = body.Instructions[i];
			var second = body.Instructions[i + 1];
			bool isLoadAvailable = first.OpCode == OpCodes.Ldsfld
				&& first.Operand == availableField;
			bool isGetCount = second.OpCode == OpCodes.Callvirt
				&& second.Operand is MethodReference methodRef
				&& methodRef.Name == "get_Count";
			if (isLoadAvailable && isGetCount)
			{
				insertBefore = first;
				break;
			}
		}
		if (insertBefore == null)
		{
			throw new InvalidOperationException(
				"Didn't find the count check in InitResolutions");
		}

		// Pseudo code for what gets written below
		//   current = Screen.currentResolution
		//   width = current.width, height = current.height
		//   alreadyInList = false
		//   for each item in list, if width,height match then alreadyInList = true, stop
		//   if not already in list, list.Add(new ResolutionConfig(width, height))
		var injection = new List<Instruction>();
		injection.Add(il.Create(OpCodes.Call, unity.ScreenCurrent));
		injection.Add(il.Create(OpCodes.Stloc, currentResolution));
		injection.Add(il.Create(OpCodes.Ldloca, currentResolution));
		injection.Add(il.Create(OpCodes.Call, unity.ResolutionWidth));
		injection.Add(il.Create(OpCodes.Stloc, monitorWidth));
		injection.Add(il.Create(OpCodes.Ldloca, currentResolution));
		injection.Add(il.Create(OpCodes.Call, unity.ResolutionHeight));
		injection.Add(il.Create(OpCodes.Stloc, monitorHeight));
		injection.Add(il.Create(OpCodes.Ldc_I4_0));
		injection.Add(il.Create(OpCodes.Stloc, alreadyInList));
		injection.Add(il.Create(OpCodes.Ldc_I4_0));
		injection.Add(il.Create(OpCodes.Stloc, loopIndex));

		var loopTop   = il.Create(OpCodes.Ldloc, loopIndex);
		var loopDone  = il.Create(OpCodes.Nop);
		var notAMatch = il.Create(OpCodes.Nop);
		var skipAdd   = il.Create(OpCodes.Nop);

		injection.Add(loopTop);
		injection.Add(il.Create(OpCodes.Ldsfld, availableField));
		injection.Add(il.Create(OpCodes.Callvirt, listCount));
		injection.Add(il.Create(OpCodes.Bge, loopDone));

		injection.Add(il.Create(OpCodes.Ldsfld, availableField));
		injection.Add(il.Create(OpCodes.Ldloc, loopIndex));
		injection.Add(il.Create(OpCodes.Callvirt, listGetItem));
		injection.Add(il.Create(OpCodes.Stloc, listItem));

		injection.Add(il.Create(OpCodes.Ldloc, listItem));
		injection.Add(il.Create(OpCodes.Ldfld, xField));
		injection.Add(il.Create(OpCodes.Ldloc, monitorWidth));
		injection.Add(il.Create(OpCodes.Bne_Un, notAMatch));
		injection.Add(il.Create(OpCodes.Ldloc, listItem));
		injection.Add(il.Create(OpCodes.Ldfld, yField));
		injection.Add(il.Create(OpCodes.Ldloc, monitorHeight));
		injection.Add(il.Create(OpCodes.Bne_Un, notAMatch));
		injection.Add(il.Create(OpCodes.Ldc_I4_1));
		injection.Add(il.Create(OpCodes.Stloc, alreadyInList));
		injection.Add(il.Create(OpCodes.Br, loopDone));

		injection.Add(notAMatch);
		injection.Add(il.Create(OpCodes.Ldloc, loopIndex));
		injection.Add(il.Create(OpCodes.Ldc_I4_1));
		injection.Add(il.Create(OpCodes.Add));
		injection.Add(il.Create(OpCodes.Stloc, loopIndex));
		injection.Add(il.Create(OpCodes.Br, loopTop));

		injection.Add(loopDone);
		injection.Add(il.Create(OpCodes.Ldloc, alreadyInList));
		injection.Add(il.Create(OpCodes.Brtrue, skipAdd));
		injection.Add(il.Create(OpCodes.Ldsfld, availableField));
		injection.Add(il.Create(OpCodes.Ldloc, monitorWidth));
		injection.Add(il.Create(OpCodes.Ldloc, monitorHeight));
		injection.Add(il.Create(OpCodes.Newobj, resolutionCtor));
		injection.Add(il.Create(OpCodes.Callvirt, listAdd));
		injection.Add(skipAdd);

		foreach (var instruction in injection)
			il.InsertBefore(insertBefore, instruction);

		Console.WriteLine("  Ok native resolution is in the in game list");
	}

	// Fog is a 6 wide 36 tall grid of tiles, each 576 pixels wide
	// At 32:9 half the screen has no fog, so swap both constants for a formula
	static void PatchFogObject(ModuleDefinition module, UnityRefs unity)
	{
		var fogObject = module.GetType("FogObject");
		if (fogObject == null) return;

		// Emits Max(FOG_BASE_GRID, CeilToInt(Screen.width / FOG_TILE_WIDTH) + FOG_EXTRA_COLUMNS)
		// Pushes an int onto the stack right before the given instruction
		Action<ILProcessor, Instruction> emitFormula = (processor, anchor) =>
		{
			processor.InsertBefore(anchor,
				processor.Create(OpCodes.Ldc_I4_6));
			processor.InsertBefore(anchor,
				processor.Create(OpCodes.Call, unity.ScreenWidth));
			processor.InsertBefore(anchor, processor.Create(OpCodes.Conv_R4));
			processor.InsertBefore(anchor,
				processor.Create(OpCodes.Ldc_R4, (float)FOG_TILE_WIDTH_PIXELS));
			processor.InsertBefore(anchor, processor.Create(OpCodes.Div));
			processor.InsertBefore(anchor,
				processor.Create(OpCodes.Call, unity.MathfCeilToInt));
			processor.InsertBefore(anchor,
				processor.Create(OpCodes.Ldc_I4_2));
			processor.InsertBefore(anchor, processor.Create(OpCodes.Add));
			processor.InsertBefore(anchor,
				processor.Create(OpCodes.Call, unity.MathMax));
		};

		ReplaceFogInitLoopLimit(fogObject, emitFormula);
		ReplaceFogUpdateWrapCheck(fogObject, emitFormula);
		ReplaceFogCtorWrapDistance(fogObject, emitFormula);

		Console.WriteLine("  Ok fog grows with screen width");
	}

	static void ReplaceFogInitLoopLimit(TypeDefinition fogObject,
		Action<ILProcessor, Instruction> emitFormula)
	{
		var method = fogObject.Methods.First(m => m.Name == "InitFog");
		CecilHelpers.PromoteShortBranches(method.Body);
		for (int i = 0; i < method.Body.Instructions.Count - 1; i++)
		{
			var instruction = method.Body.Instructions[i];
			int? value = ReadInt32Push(instruction);
			if (value != FOG_BASE_GRID) continue;

			var nextCode = method.Body.Instructions[i + 1].OpCode.Code;
			bool isLessThanBranch = nextCode == Code.Blt
				|| nextCode == Code.Blt_S
				|| nextCode == Code.Blt_Un
				|| nextCode == Code.Blt_Un_S;
			if (!isLessThanBranch) continue;

			var processor = method.Body.GetILProcessor();
			emitFormula(processor, instruction);
			processor.Remove(instruction);
			return;
		}
		throw new InvalidOperationException(
			"Didn't find the '< 6' loop in FogObject.InitFog");
	}

	static void ReplaceFogUpdateWrapCheck(TypeDefinition fogObject,
		Action<ILProcessor, Instruction> emitFormula)
	{
		var method = fogObject.Methods.First(m => m.Name == "Update");
		CecilHelpers.PromoteShortBranches(method.Body);
		for (int i = 0; i < method.Body.Instructions.Count - 2; i++)
		{
			var instruction = method.Body.Instructions[i];
			if (instruction.OpCode != OpCodes.Ldc_R4) continue;
			if (!(instruction.Operand is float value) || value != FOG_BASE_GRID) continue;

			var nextOne = method.Body.Instructions[i + 1];
			var nextTwo = method.Body.Instructions[i + 2];
			bool isBorderSub = nextOne.OpCode == OpCodes.Ldsfld
				&& nextOne.Operand is FieldReference fieldRef
				&& fieldRef.Name == "BORDER_X"
				&& nextTwo.OpCode == OpCodes.Sub;
			if (!isBorderSub) continue;

			var processor = method.Body.GetILProcessor();
			emitFormula(processor, instruction);
			processor.InsertBefore(instruction, processor.Create(OpCodes.Conv_R4));
			processor.Remove(instruction);
			return;
		}
		throw new InvalidOperationException(
			"Didn't find '6 - BORDER_X' in FogObject.Update");
	}

	static void ReplaceFogCtorWrapDistance(TypeDefinition fogObject,
		Action<ILProcessor, Instruction> emitFormula)
	{
		var method = fogObject.Methods.First(m => m.IsConstructor && !m.IsStatic);
		CecilHelpers.PromoteShortBranches(method.Body);
		for (int i = 0; i < method.Body.Instructions.Count; i++)
		{
			var instruction = method.Body.Instructions[i];
			if (instruction.OpCode != OpCodes.Ldc_R4) continue;
			if (!(instruction.Operand is float value)
				|| value != FOG_WRAP_DISTANCE) continue;

			var processor = method.Body.GetILProcessor();
			emitFormula(processor, instruction);
			processor.InsertBefore(instruction, processor.Create(OpCodes.Ldc_I4_6));
			processor.InsertBefore(instruction, processor.Create(OpCodes.Mul));
			processor.InsertBefore(instruction, processor.Create(OpCodes.Conv_R4));
			processor.Remove(instruction);
			return;
		}
		throw new InvalidOperationException(
			"Didn't find 'ldc.r4 36' in FogObject ctor");
	}

	static int? ReadInt32Push(Instruction instruction)
	{
		if (instruction.OpCode == OpCodes.Ldc_I4_6) return 6;
		if (instruction.OpCode == OpCodes.Ldc_I4 && instruction.Operand is int value)
			return value;
		if (instruction.OpCode == OpCodes.Ldc_I4_S && instruction.Operand is sbyte small)
			return small;
		return null;
	}

	// Game writes Software in two places, flip both to Hardware
	static void PatchCursorDefault(TypeDefinition gameSettings,
		TypeDefinition platformSpecific)
	{
		var methods = new[] {
			gameSettings.Methods
				.First(m => m.IsConstructor && !m.IsStatic),
			platformSpecific.Methods
				.First(m => m.Name == "LoadGameSettings"),
		};
		foreach (var method in methods)
		{
			foreach (var instruction in method.Body.Instructions.ToList())
			{
				bool isSoftwarePush = instruction.OpCode == OpCodes.Ldc_I4_2;
				bool nextStoresCursor = instruction.Next != null
					&& instruction.Next.OpCode == OpCodes.Stfld
					&& instruction.Next.Operand is FieldReference fieldRef
					&& fieldRef.Name == "cursor_mode";
				if (isSoftwarePush && nextStoresCursor)
					instruction.OpCode = OpCodes.Ldc_I4_1;
			}
		}
		Console.WriteLine("  Ok cursor defaults to Hardware");
	}


	// = Saved settings ===========================================================
	// Where Unity writes saved settings per OS
	static IEnumerable<string> SettingsPaths()
	{
		string home = GetHomeFolder();
		// Linux native
		yield return Path.Combine(home, ".config", "unity3d",
			"Lazy Bear Games", "Graveyard Keeper", "prefs");
		// Mac native
		yield return Path.Combine(home, "Library", "Preferences",
			"unity.Lazy Bear Games.Graveyard Keeper.plist");
		// Windows under Proton, Wine registry
		yield return Path.Combine(home, ".local", "share", "Steam",
			"steamapps", "compatdata", STEAM_APP_ID.ToString(),
			"pfx", "user.reg");
	}

	static void ClearSavedSettings()
	{
		Console.WriteLine();
		Console.WriteLine("Clearing saved settings");
		string home = GetHomeFolder();
		bool anythingCleared = false;

		string linuxPrefs = Path.Combine(home, ".config", "unity3d",
			"Lazy Bear Games", "Graveyard Keeper", "prefs");
		if (File.Exists(linuxPrefs))
		{
			File.Delete(linuxPrefs);
			Console.WriteLine("  Deleted " + linuxPrefs);
			anythingCleared = true;
		}

		string macPrefs = Path.Combine(home, "Library", "Preferences",
			"unity.Lazy Bear Games.Graveyard Keeper.plist");
		if (File.Exists(macPrefs))
		{
			File.Delete(macPrefs);
			Console.WriteLine("  Deleted " + macPrefs);
			anythingCleared = true;
		}

		// Proton keeps PlayerPrefs inside user.reg of the wine prefix
		string userReg = Path.Combine(home, ".local", "share", "Steam",
			"steamapps", "compatdata", STEAM_APP_ID.ToString(),
			"pfx", "user.reg");
		if (File.Exists(userReg))
		{
			const string section = @"Software\\Lazy Bear Games\\Graveyard Keeper";
			if (RemoveRegistrySection(userReg, section))
			{
				Console.WriteLine(
					"  Cleared [Software\\Lazy Bear Games\\Graveyard Keeper] in "
					+ userReg);
				anythingCleared = true;
			}
		}

		if (!anythingCleared)
			Console.WriteLine("  Nothing there to clear");
	}

	// Remove a [Software\...] section from user.reg
	// Wine tidies the file on next launch so whitespace doesn't matter
	static bool RemoveRegistrySection(string path, string sectionPath)
	{
		var lines = File.ReadAllLines(path);
		string header = "[" + sectionPath + "]";
		var output = new List<string>(lines.Length);
		bool skipping = false;
		bool removed = false;
		foreach (var line in lines)
		{
			if (skipping)
			{
				// A new section starts with [, stop skipping when we hit one
				if (line.Length > 0 && line[0] == '[')
				{
					skipping = false;
					output.Add(line);
				}
				continue;
			}
			if (line.StartsWith(header))
			{
				skipping = true;
				removed = true;
				continue;
			}
			output.Add(line);
		}
		if (removed) File.WriteAllLines(path, output);
		return removed;
	}


	// = Level2 patch =============================================================
	// Outcomes from patching the level2 scene file
	enum Level2Result
	{
		Patched,
		AlreadyPatched,
		MissingFile,
		AnchorNotFound,
		AnchorAmbiguous,
	}

	// Find the small_font UILabel in level2 and flip m_Enabled from 1 to 0
	// Match a 24 byte pattern so the offset isn't tied to one platform build
	static Level2Result PatchLevel2(string path, string backupPath)
	{
		Console.WriteLine();
		Console.WriteLine("level2 (main menu scene)");

		if (!File.Exists(path))
		{
			Console.WriteLine("  level2 not found at " + path);
			Console.WriteLine(
				"    the dll patches still applied, but I can't reach");
			Console.WriteLine(
				"    the small_font label inside the main menu scene from here");
			Console.WriteLine(
				"    run me again pointed at a full game install, not just Managed");
			return Level2Result.MissingFile;
		}

		if (!File.Exists(backupPath))
		{
			File.Copy(path, backupPath);
			Console.WriteLine("  Made backup at " + backupPath);
		}
		var bytes = File.ReadAllBytes(backupPath);

		// First 24 bytes of that UILabel look like this
		//   m_GameObject PPtr, FileID 0, PathID 14, 12 bytes
		//   m_Enabled 1 as int32, 4 bytes, this is what I flip
		//   m_Script PPtr, FileID 1, PathID 3018 which is 0xBCA, first 8 bytes
		byte[] anchor = {
			0x00, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x01, 0x00, 0x00, 0x00,
			0x01, 0x00, 0x00, 0x00, 0xCA, 0x0B, 0x00, 0x00,
		};

		int firstMatch = -1;
		int matchCount = 0;
		for (int i = 0; i + anchor.Length <= bytes.Length; i++)
		{
			bool match = true;
			for (int k = 0; k < anchor.Length; k++)
			{
				if (bytes[i + k] != anchor[k]) { match = false; break; }
			}
			if (match)
			{
				if (firstMatch < 0) firstMatch = i;
				matchCount++;
			}
		}

		if (matchCount == 0)
		{
			Console.WriteLine(
				"  Can't find the small_font UILabel pattern in level2");
			Console.WriteLine(
				"    probably a different game version than what I expect");
			Console.WriteLine(
				"    the dll patches still apply, only the debug label stays");
			return Level2Result.AnchorNotFound;
		}
		if (matchCount > 1)
		{
			Console.WriteLine(
				"  The pattern matches " + matchCount
				+ " times, not safe to pick one, skipping");
			return Level2Result.AnchorAmbiguous;
		}

		int enabledOffset = firstMatch + LEVEL2_ENABLED_OFFSET;
		if (bytes[enabledOffset] != 0x01)
		{
			Console.WriteLine("  Already patched, m_Enabled at 0x"
				+ enabledOffset.ToString("x") + " is 0x"
				+ bytes[enabledOffset].ToString("x"));
			return Level2Result.AlreadyPatched;
		}

		bytes[enabledOffset] = 0x00;
		File.WriteAllBytes(path, bytes);
		Console.WriteLine("  Ok small_font UILabel disabled, m_Enabled at 0x"
			+ enabledOffset.ToString("x") + " flipped 0x01 to 0x00");
		return Level2Result.Patched;
	}
}


// Small Cecil helpers used by the patcher
static class CecilHelpers
{
	// Change every short branch to its long form
	// Do this before editing, else the 1 byte offset can't reach its target
	public static void PromoteShortBranches(MethodBody body)
	{
		var table = new Dictionary<Code, OpCode> {
			{ Code.Br_S,      OpCodes.Br      },
			{ Code.Brtrue_S,  OpCodes.Brtrue  },
			{ Code.Brfalse_S, OpCodes.Brfalse },
			{ Code.Beq_S,     OpCodes.Beq     },
			{ Code.Bne_Un_S,  OpCodes.Bne_Un  },
			{ Code.Bge_S,     OpCodes.Bge     },
			{ Code.Bgt_S,     OpCodes.Bgt     },
			{ Code.Ble_S,     OpCodes.Ble     },
			{ Code.Blt_S,     OpCodes.Blt     },
			{ Code.Bge_Un_S,  OpCodes.Bge_Un  },
			{ Code.Bgt_Un_S,  OpCodes.Bgt_Un  },
			{ Code.Ble_Un_S,  OpCodes.Ble_Un  },
			{ Code.Blt_Un_S,  OpCodes.Blt_Un  },
			{ Code.Leave_S,   OpCodes.Leave   },
		};
		foreach (var instruction in body.Instructions)
		{
			if (table.TryGetValue(instruction.OpCode.Code, out var longForm))
				instruction.OpCode = longForm;
		}
	}

	// Cecil needs help to call a generic class method like List T Add
	// on a specific generic type, so this wraps it for that case
	public static MethodReference MakeHostInstanceGeneric(
		this MethodReference method, params TypeReference[] typeArguments)
	{
		var host = new GenericInstanceType(method.DeclaringType);
		foreach (var typeArgument in typeArguments)
			host.GenericArguments.Add(typeArgument);
		var result = new MethodReference(method.Name, method.ReturnType, host)
		{
			HasThis = method.HasThis,
			ExplicitThis = method.ExplicitThis,
			CallingConvention = method.CallingConvention,
		};
		foreach (var parameter in method.Parameters)
			result.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
		foreach (var genericParameter in method.GenericParameters)
			result.GenericParameters.Add(
				new GenericParameter(genericParameter.Name, result));
		return result;
	}
}
