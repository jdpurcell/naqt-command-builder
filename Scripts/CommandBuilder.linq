<Query Kind="Program">
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Security.Cryptography</Namespace>
  <DisableMyExtensions>true</DisableMyExtensions>
</Query>

readonly string PathQtXmlDir = $@"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}/GitHub/qtsdkrepository";

IEnumerable<(string Host, string Target, Version Version, string Arch)> AllItems =
	from host in Hosts
	from target in GetRelevantTargets(host)
	from version in GetRelevantVersions(host, target)
	from arch in GetRelevantArches(host, target, version)
	select (host, target, version, arch);

void Main() {
	//GenerateModuleLookups(false);
	//TestGeneratedModules();
	//FetchUpdateXmls();
}

void GenerateModuleLookups(bool makeJavaScript) {
	Dictionary<string, string> codeByAlias = [];
	List<(string Name, string[] Keys)> ModuleAliases = [
		("ModulesMsvc2015", [
			"windows.desktop.win64_msvc2015_64"
		]),
		("ModulesMsvc", [
			"windows.desktop.win32_msvc2017",
			"windows.desktop.win64_msvc2017_64",
			"windows.desktop.win32_msvc2019",
			"windows.desktop.win64_msvc2019_64",
			"windows.desktop.win64_msvc2022_64"
		]),
		("ModulesMsvcArmCc", [
			"windows.desktop.win64_msvc2019_arm64",
			"windows.desktop.win64_msvc2022_arm64_cross_compiled"
		]),
		("ModulesMsvcArm", [
			"windows_arm64.desktop.win64_msvc2022_arm64"
		]),
		("ModulesMingw", [
			"windows.desktop.win32_mingw73",
			"windows.desktop.win64_mingw73",
			"windows.desktop.win32_mingw81",
			"windows.desktop.win64_mingw81",
			"windows.desktop.win64_mingw",
			"windows.desktop.win64_llvm_mingw"
		]),
		("ModulesLinuxGcc", [
			"linux.desktop.gcc_64",
			"linux.desktop.linux_gcc_64",
			"linux_arm64.desktop.linux_gcc_arm64"
		]),
		("ModulesMac", [
			"mac.desktop.clang_64"
		]),
		("ModulesWasmSt", [
			"wasm.wasm_32",
			"wasm.wasm_singlethread"
		]),
		("ModulesWasmMt", [
			"wasm.wasm_multithread"
		]),
		("ModulesAndroid", [
			"android.android",
			"android.android_armv7",
			"android.android_arm64_v8a",
			"android.android_x86",
			"android.android_x86_64"
		]),
		("ModulesIos", [
			"ios.ios"
		]),
	];
	string GetAlias(string platform, string arch) {
		string key = $"{platform}.{arch}";
		foreach ((string name, string[] keys) in ModuleAliases) {
			if (keys.Contains(key))
				return name;
		}
		throw new Exception($"Undefined alias for {key}");
	}
	foreach (var mainGroup in AllItems.GroupBy(n => GetAlias(MakePlatform(n.Host, n.Target), n.Arch))) {
		Version minVersion = mainGroup.Min(n => n.Version);
		Dictionary<string, List<VersionRange>> moduleStats = [];
		foreach (var versionGroup in mainGroup.GroupBy(g => g.Version).OrderBy(g => g.Key)) {
			string[] GetModules((string Host, string Target, Version Version, string Arch) item) {
				string relativeDir = GetUpdateDirectoryUrl(item.Host, item.Target, item.Version, item.Arch).SubstringAfterFirst("/qtsdkrepository/").TrimEnd('/');
				XDocument updateDoc = XDocument.Parse(File.ReadAllText(Path.Combine(PathQtXmlDir, relativeDir, "Updates.xml")));
				return [..ParseModulesFromUpdate(updateDoc, item.Arch).Where(n => !IsExcludedModule(n)).Order()];
			}
			string[] modules = GetModules(versionGroup.First());
			if (versionGroup.Skip(1).Any(n => !GetModules(n).SequenceEqual(modules))) {
				throw new Exception("Modules mismatch!");
			}
			Version v = versionGroup.First().Version;
			foreach (string module in modules) {
				if (!moduleStats.ContainsKey(module)) {
					// New module, add to dictionary
					moduleStats.Add(module, [new VersionRange(v == minVersion ? null : v, null)]);
				}
				else if (moduleStats[module].Last().End != null) {
					// Reintroduced module, add to existing list
					moduleStats[module].Add(new VersionRange(v == minVersion ? null : v, null));
				}
			}
			foreach (string module in moduleStats.Where(n => n.Value.Last().End == null).Select(n => n.Key).Except(modules)) {
				// Dropped module
				moduleStats[module].Last().End = mainGroup.Key == GetAlias("wasm", "wasm_32") && v == new Version(6, 2, 0) ? new Version(6, 0, 0) : v;
			}
		}
		StringBuilder sb = new();
		if (makeJavaScript) {
			sb.AppendLine($"\"{mainGroup.Key}\": [");
			foreach ((string module, List<VersionRange> ranges) in moduleStats.OrderBy(n => n.Key)) {
				sb.Append($"  QtMod(\"{module}\"");
				if (ranges.Any(r => r.IsConstrained)) sb.Append($", v => {ranges.Select(r => r.ToConditionJs()).StringJoin(" || ")}");
				sb.AppendLine("),");
			}
			sb.AppendLine("],");
		}
		else {
			sb.AppendLine($"[\"{mainGroup.Key}\"] = [");
			foreach ((string module, List<VersionRange> ranges) in moduleStats.OrderBy(n => n.Key)) {
				sb.Append($"\tnew(\"{module}\"");
				if (ranges.Any(r => r.IsConstrained)) sb.Append($", v => {ranges.Select(r => r.ToCondition()).StringJoin(" || ")}");
				sb.AppendLine("),");
			}
			sb.AppendLine("],");
		}
		codeByAlias[mainGroup.Key] = sb.ToString();
	}
	string result = "";
	foreach ((string name, _) in ModuleAliases) {
		result += codeByAlias[name];
	}
	result += "\n// ----------\n\n";
	foreach ((string platform, QtArch[] arches) in ArchesByPlatform) {
		foreach (string arch in arches.Select(a => a.Name)) {
			string alias = GetAlias(platform, arch);
			if (makeJavaScript) {
				result += $"\"{platform}.{arch}\": ModulesByAlias[\"{alias}\"],\n";
			}
			else {
				result += $"[\"{platform}.{arch}\"] = ModulesByAlias[\"{alias}\"],\n";
			}
		}
	}
	result.Dump();
}

void TestGeneratedModules() {
	int passCount = 0;
	int failCount = 0;
	foreach (var (host, target, version, arch) in AllItems) {
		string relativeDir = GetUpdateDirectoryUrl(host, target, version, arch).SubstringAfterFirst("/qtsdkrepository/").TrimEnd('/');
		XDocument updateDoc = XDocument.Parse(File.ReadAllText(Path.Combine(PathQtXmlDir, relativeDir, "Updates.xml")));
		string[] expectedModules = ParseModulesFromUpdate(updateDoc, arch).Where(n => !IsExcludedModule(n)).Order().ToArray();
		string[] generatedModules = GetRelevantModules(host, target, version, arch).Order().ToArray();
		if (expectedModules.SequenceEqual(generatedModules))
			passCount++;
		else
			failCount++;
	}
	$"Pass: {passCount}".Dump();
	$"Fail: {failCount}".Dump();
	return;
}

bool IsExcludedModule(string name) =>
	name == "debug_info" || name.EndsWith(".debug_information");

void FetchUpdateXmls() {
	foreach (var (host, target, version, arch) in AllItems) {
		string updateDirUrl = GetUpdateDirectoryUrl(host, target, version, arch);
		string relativeDir = updateDirUrl.SubstringAfterFirst("/qtsdkrepository/").TrimEnd('/');
		string localDir = Path.Combine(PathQtXmlDir, relativeDir);
		Directory.CreateDirectory(localDir);
		string xmlPath = Path.Combine(localDir, "Updates.xml");
		string hashPath = Path.Combine(localDir, "Updates.xml.sha256");
		if (File.Exists(xmlPath)) continue;
		File.WriteAllBytes(xmlPath, FetchAsBytes(updateDirUrl + "Updates.xml"));
		File.WriteAllBytes(hashPath, FetchAsBytes(updateDirUrl + "Updates.xml.sha256"));
		byte[] expectedHash = Convert.FromHexString(File.ReadAllText(hashPath).SubstringBeforeFirst(" "));
		byte[] actualHash = SHA256.HashData(File.ReadAllBytes(xmlPath));
		if (!actualHash.SequenceEqual(expectedHash)) throw new Exception("Hash mismatch!");
		relativeDir.Dump();
	}
}

static class ExtensionMethods {
	public static string StringJoin(this IEnumerable<string> values, string separator) =>
		String.Join(separator, values);

	public static string SubstringBeforeFirst(this string str, string delim) =>
		str.Substring(0, str.IndexOf(delim, StringComparison.Ordinal).ThrowIfLtZero());

	public static string SubstringAfterFirst(this string str, string delim) =>
		str.Substring(str.IndexOf(delim, StringComparison.Ordinal).ThrowIfLtZero() + delim.Length);

	public static int ThrowIfLtZero(this int value) =>
		value >= 0 ? value : throw new ArgumentOutOfRangeException();

	public static bool IsAtLeast(this Version version, int major, int minor, int revision) =>
		version >= new Version(major, minor, revision);

	public static bool IsUnder(this Version version, int major, int minor, int revision) =>
		version < new Version(major, minor, revision);

	public static bool IsBetween(this Version version, (int major, int minor, int revision) start, (int major, int minor, int revision) end) =>
		version.IsAtLeast(start.major, start.minor, start.revision) && version.IsUnder(end.major, end.minor, end.revision);
}

class VersionRange(Version start, Version end) {
	public Version Start { get; set; } = start;
	public Version End { get; set; } = end;

	public bool IsConstrained => Start != null || End != null;

	public string ToCondition() =>
		Start != null && End != null ? $"v.IsBetween(({Start.Major}, {Start.Minor}, {Start.Build}), ({End.Major}, {End.Minor}, {End.Build}))" :
		Start != null ? $"v.IsAtLeast({Start.Major}, {Start.Minor}, {Start.Build})" :
		End != null ? $"v.IsUnder({End.Major}, {End.Minor}, {End.Build})" :
		null;

	public string ToConditionJs() =>
		Start != null && End != null ? $"btw(v, V({Start.Major},{Start.Minor},{Start.Build}), V({End.Major},{End.Minor},{End.Build}))" :
		Start != null ? $"gte(v, V({Start.Major},{Start.Minor},{Start.Build}))" :
		End != null ? $"lt(v, V({End.Major},{End.Minor},{End.Build}))" :
		null;
}

record QtRelease(int Major, int Minor, int LastPatch);

static QtRelease[] Releases = [
	new(5, 12, 12),
	new(5, 13, 2),
	new(5, 14, 2),
	new(5, 15, 2),
	new(6, 0, 4),
	new(6, 1, 3),
	new(6, 2, 4),
	new(6, 3, 2),
	new(6, 4, 3),
	new(6, 5, 3),
	new(6, 6, 3),
	new(6, 7, 3),
	new(6, 8, 3),
	new(6, 9, 3),
	new(6, 10, 2)
];

record QtArch(string Name, Func<Version, bool> IsAvailable);

static Dictionary<string, QtArch[]> ArchesByPlatform = new() {
	["windows.desktop"] = [
		new("win64_msvc2022_64", v => v.IsAtLeast(6, 8, 0)),
		new("win64_msvc2022_arm64_cross_compiled", v => v.IsAtLeast(6, 8, 0)),
		new("win64_msvc2019_64", v => v.IsBetween((5, 15, 0), (6, 8, 0))),
		new("win64_msvc2019_arm64", v => v.IsBetween((6, 2, 0), (6, 8, 0))),
		new("win32_msvc2019", v => v.IsBetween((5, 15, 0), (6, 0, 0))),
		new("win64_msvc2017_64", v => v.IsUnder(5, 15, 0)),
		new("win32_msvc2017", v => v.IsUnder(5, 15, 0)),
		new("win64_msvc2015_64", v => v.IsUnder(6, 0, 0)),
		new("win64_mingw", v => v.IsAtLeast(6, 2, 2)),
		new("win64_llvm_mingw", v => v.IsAtLeast(6, 7, 0)),
		new("win64_mingw81", v => v.IsBetween((5, 15, 0), (6, 2, 2))),
		new("win32_mingw81", v => v.IsBetween((5, 15, 0), (6, 0, 0))),
		new("win64_mingw73", v => v.IsUnder(5, 15, 0)),
		new("win32_mingw73", v => v.IsBetween((5, 12, 2), (5, 15, 0))),
	],
	["windows_arm64.desktop"] = [
		new("win64_msvc2022_arm64", v => v.IsAtLeast(6, 8, 0))
	],
	["linux.desktop"] = [
		new("linux_gcc_64", v => v.IsAtLeast(6, 7, 0)),
		new("gcc_64", v => v.IsUnder(6, 7, 0)),
	],
	["linux_arm64.desktop"] = [
		new("linux_gcc_arm64", v => v.IsAtLeast(6, 7, 0))
	],
	["mac.desktop"] = [
		new("clang_64", v => true)
	],
	["wasm"] = [
		new("wasm_singlethread", v => v.IsAtLeast(6, 5, 0)),
		new("wasm_multithread", v => v.IsAtLeast(6, 5, 0)),
		new("wasm_32", v => v.IsBetween((5, 13, 0), (6, 5, 0)) && !v.IsBetween((6, 0, 0), (6, 2, 0))),
	],
	["android"] = [
		new("android_arm64_v8a", v => !v.IsBetween((5, 14, 0), (6, 0, 0))),
		new("android_armv7", v => !v.IsBetween((5, 14, 0), (6, 0, 0))),
		new("android_x86", v => !v.IsBetween((5, 14, 0), (6, 0, 0))),
		new("android_x86_64", v => v.IsAtLeast(5, 13, 0) && !v.IsBetween((5, 14, 0), (6, 0, 0))),
		new("android", v => v.IsBetween((5, 14, 0), (6, 0, 0)))
	],
	["ios"] = [
		new("ios", v => true)
	]
};

record QtModule(string Name, Func<Version, bool> IsAvailable = null);

static Dictionary<string, QtModule[]> ModulesByAlias = new() {
	["ModulesMsvc2015"] = [
		new("qtcharts"),
		new("qtdatavis3d"),
		new("qtlottie", v => v.IsAtLeast(5, 13, 0)),
		new("qtnetworkauth"),
		new("qtpurchasing"),
		new("qtquick3d", v => v.IsAtLeast(5, 14, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtscript"),
		new("qtvirtualkeyboard"),
		new("qtwebglplugin"),
	],
	["ModulesMsvc"] = [
		new("qt3d", v => v.IsAtLeast(6, 1, 0)),
		new("qt5compat", v => v.IsAtLeast(6, 0, 0)),
		new("qtactiveqt", v => v.IsAtLeast(6, 1, 0)),
		new("qtcharts", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtconnectivity", v => v.IsAtLeast(6, 2, 0)),
		new("qtdatavis3d", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 1, 0)),
		new("qtlanguageserver", v => v.IsAtLeast(6, 3, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie", v => v.IsBetween((5, 13, 0), (6, 0, 0)) || v.IsAtLeast(6, 1, 0)),
		new("qtmultimedia", v => v.IsAtLeast(6, 2, 0)),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtpdf", v => v.IsBetween((6, 3, 0), (6, 8, 0))),
		new("qtpositioning", v => v.IsAtLeast(6, 2, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(5, 14, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquickeffectmaker", v => v.IsAtLeast(6, 5, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtremoteobjects", v => v.IsAtLeast(6, 2, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 1, 0)),
		new("qtsensors", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialbus", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialport", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 0, 0)),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebengine", v => v.IsUnder(6, 0, 0) || v.IsBetween((6, 2, 0), (6, 8, 0))),
		new("qtwebglplugin", v => v.IsUnder(6, 0, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 2, 0)),
	],
	["ModulesMsvcArmCc"] = [
		new("qt3d"),
		new("qt5compat"),
		new("qtactiveqt"),
		new("qtcharts"),
		new("qtconnectivity"),
		new("qtdatavis3d"),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats"),
		new("qtlanguageserver", v => v.IsAtLeast(6, 3, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie"),
		new("qtmultimedia"),
		new("qtnetworkauth"),
		new("qtpositioning"),
		new("qtquick3d"),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquicktimeline"),
		new("qtremoteobjects"),
		new("qtscxml"),
		new("qtsensors"),
		new("qtserialbus"),
		new("qtserialport"),
		new("qtshadertools"),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard"),
		new("qtwebchannel"),
		new("qtwebsockets"),
		new("qtwebview", v => v.IsBetween((6, 3, 0), (6, 4, 0)) || v.IsAtLeast(6, 9, 3)),
	],
	["ModulesMsvcArm"] = [
		new("qt3d", v => v.IsAtLeast(6, 10, 0)),
		new("qt5compat"),
		new("qtactiveqt"),
		new("qtcharts"),
		new("qtconnectivity"),
		new("qtdatavis3d", v => v.IsAtLeast(6, 10, 0)),
		new("qtgraphs"),
		new("qtgrpc"),
		new("qthttpserver"),
		new("qtimageformats"),
		new("qtlanguageserver"),
		new("qtlocation"),
		new("qtlottie"),
		new("qtmultimedia"),
		new("qtnetworkauth"),
		new("qtpositioning"),
		new("qtquick3d"),
		new("qtquick3dphysics"),
		new("qtquickeffectmaker", v => v.IsAtLeast(6, 10, 0)),
		new("qtquicktimeline"),
		new("qtremoteobjects"),
		new("qtscxml"),
		new("qtsensors"),
		new("qtserialbus"),
		new("qtserialport"),
		new("qtshadertools"),
		new("qtspeech"),
		new("qtvirtualkeyboard"),
		new("qtwebchannel"),
		new("qtwebsockets"),
		new("qtwebview", v => v.IsAtLeast(6, 9, 3)),
	],
	["ModulesMingw"] = [
		new("qt3d", v => v.IsAtLeast(6, 1, 0)),
		new("qt5compat", v => v.IsAtLeast(6, 0, 0)),
		new("qtactiveqt", v => v.IsAtLeast(6, 1, 0)),
		new("qtcharts", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtconnectivity", v => v.IsAtLeast(6, 2, 0)),
		new("qtdatavis3d", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 1, 0)),
		new("qtlanguageserver", v => v.IsAtLeast(6, 3, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie", v => v.IsBetween((5, 13, 0), (6, 0, 0)) || v.IsAtLeast(6, 1, 0)),
		new("qtmultimedia", v => v.IsAtLeast(6, 2, 0)),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtpdf", v => v.IsBetween((6, 6, 0), (6, 8, 0))),
		new("qtpositioning", v => v.IsAtLeast(6, 2, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(5, 14, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquickeffectmaker", v => v.IsAtLeast(6, 5, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtremoteobjects", v => v.IsAtLeast(6, 2, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 1, 0)),
		new("qtsensors", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialbus", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialport", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 0, 0)),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebglplugin", v => v.IsUnder(6, 0, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 2, 0)),
	],
	["ModulesLinuxGcc"] = [
		new("qt3d", v => v.IsAtLeast(6, 1, 0)),
		new("qt5compat", v => v.IsAtLeast(6, 0, 0)),
		new("qtcharts", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtconnectivity", v => v.IsAtLeast(6, 2, 0)),
		new("qtdatavis3d", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 1, 0)),
		new("qtlanguageserver", v => v.IsAtLeast(6, 3, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie", v => v.IsBetween((5, 13, 0), (6, 0, 0)) || v.IsAtLeast(6, 1, 0)),
		new("qtmultimedia", v => v.IsAtLeast(6, 2, 0)),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtpdf", v => v.IsBetween((6, 3, 0), (6, 8, 0))),
		new("qtpositioning", v => v.IsAtLeast(6, 2, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(5, 14, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquickeffectmaker", v => v.IsAtLeast(6, 5, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtremoteobjects", v => v.IsAtLeast(6, 2, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 1, 0)),
		new("qtsensors", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialbus", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialport", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 0, 0)),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtwaylandcompositor", v => v.IsAtLeast(5, 14, 0)),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebengine", v => v.IsUnder(6, 0, 0) || v.IsBetween((6, 2, 0), (6, 8, 0))),
		new("qtwebglplugin", v => v.IsUnder(6, 0, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 2, 0)),
	],
	["ModulesMac"] = [
		new("qt3d", v => v.IsAtLeast(6, 1, 0)),
		new("qt5compat", v => v.IsAtLeast(6, 0, 0)),
		new("qtcharts", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtconnectivity", v => v.IsAtLeast(6, 2, 0)),
		new("qtdatavis3d", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 1, 0)),
		new("qtlanguageserver", v => v.IsAtLeast(6, 3, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie", v => v.IsBetween((5, 13, 0), (6, 0, 0)) || v.IsAtLeast(6, 1, 0)),
		new("qtmultimedia", v => v.IsAtLeast(6, 2, 0)),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtpdf", v => v.IsBetween((6, 3, 0), (6, 8, 0))),
		new("qtpositioning", v => v.IsAtLeast(6, 2, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(5, 14, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquickeffectmaker", v => v.IsAtLeast(6, 5, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtremoteobjects", v => v.IsAtLeast(6, 2, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 1, 0)),
		new("qtsensors", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialbus", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialport", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 0, 0)),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebengine", v => v.IsUnder(6, 0, 0) || v.IsBetween((6, 2, 0), (6, 8, 0))),
		new("qtwebglplugin", v => v.IsUnder(6, 0, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 2, 0)),
	],
	["ModulesWasmSt"] = [
		new("qt5compat", v => v.IsAtLeast(6, 2, 0)),
		new("qtcharts"),
		new("qtdatavis3d"),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 2, 0)),
		new("qtlottie"),
		new("qtmultimedia", v => v.IsBetween((6, 2, 0), (6, 10, 0))),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(6, 2, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 2, 0)),
		new("qtspeech", v => v.IsBetween((6, 4, 0), (6, 10, 0))),
		new("qtvirtualkeyboard"),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebglplugin", v => v.IsUnder(6, 0, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 6, 2)),
	],
	["ModulesWasmMt"] = [
		new("qt5compat"),
		new("qtcharts"),
		new("qtdatavis3d"),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc"),
		new("qthttpserver"),
		new("qtimageformats"),
		new("qtlottie"),
		new("qtmultimedia"),
		new("qtquick3d"),
		new("qtquick3dphysics"),
		new("qtquicktimeline"),
		new("qtscxml"),
		new("qtshadertools"),
		new("qtspeech"),
		new("qtvirtualkeyboard"),
		new("qtwebchannel"),
		new("qtwebsockets"),
		new("qtwebview", v => v.IsAtLeast(6, 6, 2)),
	],
	["ModulesAndroid"] = [
		new("qt3d", v => v.IsAtLeast(6, 1, 0)),
		new("qt5compat", v => v.IsAtLeast(6, 0, 0)),
		new("qtcharts", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtconnectivity", v => v.IsAtLeast(6, 2, 0)),
		new("qtdatavis3d", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 1, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie", v => v.IsBetween((5, 13, 0), (6, 0, 0)) || v.IsAtLeast(6, 1, 0)),
		new("qtmultimedia", v => v.IsAtLeast(6, 2, 0)),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtpdf", v => v.IsBetween((6, 7, 0), (6, 8, 0))),
		new("qtpositioning", v => v.IsAtLeast(6, 2, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(5, 15, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtremoteobjects", v => v.IsAtLeast(6, 2, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 1, 0)),
		new("qtsensors", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialbus", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialport", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 0, 0)),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard", v => v.IsAtLeast(6, 1, 0)),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 2, 0)),
	],
	["ModulesIos"] = [
		new("qt3d", v => v.IsAtLeast(6, 1, 0)),
		new("qt5compat", v => v.IsAtLeast(6, 0, 0)),
		new("qtcharts", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtconnectivity", v => v.IsAtLeast(6, 2, 0)),
		new("qtdatavis3d", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtgraphs", v => v.IsAtLeast(6, 6, 0)),
		new("qtgrpc", v => v.IsAtLeast(6, 5, 0)),
		new("qthttpserver", v => v.IsAtLeast(6, 4, 0)),
		new("qtimageformats", v => v.IsAtLeast(6, 1, 0)),
		new("qtlocation", v => v.IsAtLeast(6, 5, 0)),
		new("qtlottie", v => v.IsBetween((5, 13, 0), (6, 0, 0)) || v.IsAtLeast(6, 1, 0)),
		new("qtmultimedia", v => v.IsAtLeast(6, 2, 0)),
		new("qtnetworkauth", v => v.IsUnder(6, 0, 0) || v.IsAtLeast(6, 1, 0)),
		new("qtpdf", v => v.IsBetween((6, 3, 0), (6, 8, 0))),
		new("qtpositioning", v => v.IsAtLeast(6, 2, 0)),
		new("qtpurchasing", v => v.IsUnder(6, 0, 0)),
		new("qtquick3d", v => v.IsAtLeast(6, 0, 0)),
		new("qtquick3dphysics", v => v.IsAtLeast(6, 4, 0)),
		new("qtquicktimeline", v => v.IsAtLeast(5, 14, 0)),
		new("qtremoteobjects", v => v.IsAtLeast(6, 2, 0)),
		new("qtscript", v => v.IsUnder(6, 0, 0)),
		new("qtscxml", v => v.IsAtLeast(6, 1, 0)),
		new("qtsensors", v => v.IsAtLeast(6, 2, 0)),
		new("qtserialbus", v => v.IsAtLeast(6, 2, 0)),
		new("qtshadertools", v => v.IsAtLeast(6, 0, 0)),
		new("qtspeech", v => v.IsAtLeast(6, 4, 0)),
		new("qtvirtualkeyboard", v => v.IsAtLeast(6, 1, 0)),
		new("qtwebchannel", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebsockets", v => v.IsAtLeast(6, 2, 0)),
		new("qtwebview", v => v.IsAtLeast(6, 2, 1)),
	],
};

static Dictionary<string, QtModule[]> ModulesByPlatformAndArch = new() {
	["windows.desktop.win64_msvc2022_64"] = ModulesByAlias["ModulesMsvc"],
	["windows.desktop.win64_msvc2022_arm64_cross_compiled"] = ModulesByAlias["ModulesMsvcArmCc"],
	["windows.desktop.win64_msvc2019_64"] = ModulesByAlias["ModulesMsvc"],
	["windows.desktop.win64_msvc2019_arm64"] = ModulesByAlias["ModulesMsvcArmCc"],
	["windows.desktop.win32_msvc2019"] = ModulesByAlias["ModulesMsvc"],
	["windows.desktop.win64_msvc2017_64"] = ModulesByAlias["ModulesMsvc"],
	["windows.desktop.win32_msvc2017"] = ModulesByAlias["ModulesMsvc"],
	["windows.desktop.win64_msvc2015_64"] = ModulesByAlias["ModulesMsvc2015"],
	["windows.desktop.win64_mingw"] = ModulesByAlias["ModulesMingw"],
	["windows.desktop.win64_llvm_mingw"] = ModulesByAlias["ModulesMingw"],
	["windows.desktop.win64_mingw81"] = ModulesByAlias["ModulesMingw"],
	["windows.desktop.win32_mingw81"] = ModulesByAlias["ModulesMingw"],
	["windows.desktop.win64_mingw73"] = ModulesByAlias["ModulesMingw"],
	["windows.desktop.win32_mingw73"] = ModulesByAlias["ModulesMingw"],
	["windows_arm64.desktop.win64_msvc2022_arm64"] = ModulesByAlias["ModulesMsvcArm"],
	["linux.desktop.linux_gcc_64"] = ModulesByAlias["ModulesLinuxGcc"],
	["linux.desktop.gcc_64"] = ModulesByAlias["ModulesLinuxGcc"],
	["linux_arm64.desktop.linux_gcc_arm64"] = ModulesByAlias["ModulesLinuxGcc"],
	["mac.desktop.clang_64"] = ModulesByAlias["ModulesMac"],
	["wasm.wasm_singlethread"] = ModulesByAlias["ModulesWasmSt"],
	["wasm.wasm_multithread"] = ModulesByAlias["ModulesWasmMt"],
	["wasm.wasm_32"] = ModulesByAlias["ModulesWasmSt"],
	["android.android_arm64_v8a"] = ModulesByAlias["ModulesAndroid"],
	["android.android_armv7"] = ModulesByAlias["ModulesAndroid"],
	["android.android_x86"] = ModulesByAlias["ModulesAndroid"],
	["android.android_x86_64"] = ModulesByAlias["ModulesAndroid"],
	["android.android"] = ModulesByAlias["ModulesAndroid"],
	["ios.ios"] = ModulesByAlias["ModulesIos"],
};

record QtExtension(string Name, Func<Version, bool> IsAvailable);

static Dictionary<string, QtExtension[]> ExtensionsByPlatformAndArch = new() {
	["windows.desktop.win64_msvc2022_64"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0)),
		new("qtwebengine", v => v.IsAtLeast(6, 8, 0))
	],
	["windows.desktop.win64_msvc2022_arm64_cross_compiled"] = [
		new("qtpdf", v => v.IsAtLeast(6, 9, 2)),
		new("qtwebengine", v => v.IsAtLeast(6, 9, 2))
	],
	["windows.desktop.win64_mingw"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	],
	["windows.desktop.win64_llvm_mingw"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	],
	["windows_arm64.desktop.win64_msvc2022_arm64"] = [
		new("qtpdf", v => v.IsAtLeast(6, 9, 2)),
		new("qtwebengine", v => v.IsAtLeast(6, 10, 0))
	],
	["mac.desktop.clang_64"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0)),
		new("qtwebengine", v => v.IsAtLeast(6, 8, 0))
	],
	["linux.desktop.linux_gcc_64"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0)),
		new("qtwebengine", v => v.IsAtLeast(6, 8, 0))
	],
	["linux_arm64.desktop.linux_gcc_arm64"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0)),
		new("qtwebengine", v => v.IsAtLeast(6, 8, 0))
	],
	["android.android_arm64_v8a"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	],
	["android.android_armv7"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	],
	["android.android_x86"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	],
	["android.android_x86_64"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	],
	["ios.ios"] = [
		new("qtpdf", v => v.IsAtLeast(6, 8, 0))
	]
};

public static string[] Hosts = [
	"windows",
	"windows_arm64",
	"linux",
	"linux_arm64",
	"mac"
];

public static string[] Targets = [
	"desktop",
	"wasm",
	"android",
	"ios"
];

public static Version[] Versions = [..
	from rel in Releases
	from patch in Enumerable.Range(0, rel.LastPatch + 1)
	select new Version(rel.Major, rel.Minor, patch)
];

public static IEnumerable<string> GetRelevantTargets(string host) {
	foreach (string target in Targets) {
		if (target == "ios" && host != "mac") continue;
		yield return target;
	}
}

public static IEnumerable<Version> GetRelevantVersions(string host, string target) {
	bool isCrossCompile = target != "desktop";
	QtArch[] arches = ArchesByPlatform[MakePlatform(host, target)];
	QtArch[] desktopArches = isCrossCompile ? ArchesByPlatform[MakePlatform(host, "desktop")] : [];
	foreach (Version version in Versions) {
		if (!arches.Any(a => a.IsAvailable(version))) continue;
		if (isCrossCompile && !desktopArches.Any(a => a.IsAvailable(version))) continue;
		if (target == "wasm" && version == new Version(5, 13, 0) && host != "linux") continue;
		yield return version;
	}
}

public static IEnumerable<string> GetRelevantArches(string host, string target, Version version) {
	foreach (QtArch arch in ArchesByPlatform[MakePlatform(host, target)]) {
		if (!arch.IsAvailable(version)) continue;
		yield return arch.Name;
	}
}

public static IEnumerable<string> GetRelevantModules(string host, string target, Version version, string arch) {
	QtModule[] modules = ModulesByPlatformAndArch.GetValueOrDefault($"{MakePlatform(host, target)}.{arch}") ?? [];
	foreach (QtModule module in modules) {
		if (module.IsAvailable?.Invoke(version) == false) continue;
		yield return module.Name;
	}
}

public static IEnumerable<string> GetRelevantExtensions(string host, string target, Version version, string arch) {
	QtExtension[] extensions = ExtensionsByPlatformAndArch.GetValueOrDefault($"{MakePlatform(host, target)}.{arch}") ?? [];
	foreach (QtExtension extension in extensions) {
		if (!extension.IsAvailable(version)) continue;
		yield return extension.Name;
	}
}

public static string MakePlatform(string host, string target) {
	return target == "desktop" ? $"{host}.{target}" : target;
}

public static bool UsesAllOsHost(string target, Version version) {
	if (target == "wasm" && version >= new Version(6, 7, 0)) return true;
	if (target == "android" && version >= new Version(6, 7, 0)) return true;
	return false;
}

public static string GetInstallCommand(string host, string target, Version version, string arch) {
	string targetHost = UsesAllOsHost(target, version) ? "all_os" : host;
	bool isCrossCompile = target != "desktop" || arch == "win64_msvc2019_arm64" || arch.EndsWith("_cross_compiled");
	string[] extraArgs = isCrossCompile ? ["--autodesktop"] : [];
	string command = $"install-qt {targetHost} {target} {version} {arch}";
	if (extraArgs.Length != 0) {
		command += " " + String.Join(" ", extraArgs);
	}
	return command;
}

public static string GetUpdateDirectoryUrl(string host, string target, Version version, string arch) {
	string actualHost =
		UsesAllOsHost(target, version) ? "all_os" :
		host == "windows" ? $"{host}_x86" :
		host == "linux" || host == "mac" ? $"{host}_x64" :
		host;
	string actualTarget =
		target == "wasm" && version < new Version(6, 7, 0) ? "desktop" :
		target;
	string variant =
		host == "windows" && target == "desktop" && version >= new Version(6, 11, 0) && arch.StartsWith("win64_") ? arch.Substring("win64_".Length) :
		target == "android" && version >= new Version(6, 0, 0) && arch.StartsWith("android_") ? arch.Substring("android_".Length) :
		target == "wasm" ? (version >= new Version(6, 5, 0) ? arch : "wasm") :
		"";
	string dirForVersion = $"qt{version.Major}_{version.Major}{version.Minor}{version.Build}";
	string dirForVersionAndVariant = variant.Length != 0 ? $"{dirForVersion}_{variant}" : dirForVersion;
	string actualDir =
		version >= new Version(6, 8, 0) ? $"{dirForVersion}/{dirForVersionAndVariant}" :
		dirForVersionAndVariant;
	return $"https://download.qt.io/online/qtsdkrepository/{actualHost}/{actualTarget}/{actualDir}/";
}

public static string FetchAsString(string url) {
	return new HttpClient().GetStringAsync(url).GetAwaiter().GetResult();
}

public static byte[] FetchAsBytes(string url) {
	return new HttpClient().GetByteArrayAsync(url).GetAwaiter().GetResult();
}

public static string[] ParseModulesFromUpdate(XDocument updateDoc, string arch) {
	return [..
		from package in updateDoc.Descendants("PackageUpdate")
		let nameSegments = package.Element("Name")!.Value.Split('.')
		where nameSegments.Length >= 5 && nameSegments[^1] == arch
		let startSegment = nameSegments.Length >= 6 && nameSegments[3] == "addons" ? 4 : 3
		select String.Join('.', nameSegments[startSegment..^1])
	];
}
