using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace OniCommon
{
	/// <summary>
	/// Hooks the mod's strings into the game the same way the game hooks in its own: LocString fields get
	/// the translation from a .po file, and the texts go into the common string table. Done right after the game has chosen
	/// the language (Localization.Initialize): mods are loaded earlier.
	/// </summary>
	internal static class ModLocalization
	{
		private const string KeyRoot = "STRINGS.";

		private static Type root;

		/// <summary>root: the class with the mod's strings; its name serves as the root of the keys (STRINGS.&lt;name&gt;...).</summary>
		internal static void Apply(Harmony harmony, Type stringsRoot)
		{
			root = stringsRoot;
			harmony.Patch(AccessTools.Method(typeof(Localization), nameof(Localization.Initialize)),
				postfix: new HarmonyMethod(typeof(ModLocalization), nameof(Load)));
		}

		private static void Load()
		{
			try
			{
				// Language codes of the built-in translations may have a suffix ("zh_klei"); the mod's files are named after the first part.
				string code = Localization.GetLocale()?.Code;
				if (string.IsNullOrEmpty(code))
				{
					code = Localization.GetCurrentLanguageCode();
				}
				code = string.IsNullOrEmpty(code) ? "en" : code.Split('_', '-')[0].ToLowerInvariant();
				// Checking a translation without changing the game language: the file verify_strings_language.txt in the mod folder with a language code.
				string forced = Path.Combine(ModContext.Folder, "verify_strings_language.txt");
				if (File.Exists(forced))
				{
					code = File.ReadAllText(forced).Trim().ToLowerInvariant();
				}
				string file = Path.Combine(ModContext.Folder, "translations", code + ".po");
				bool translated = File.Exists(file);
				if (translated)
				{
					Dictionary<string, string> strings = Localization.LoadStringsFile(file, false);
					string parameterErrors = "", linkErrors = "", linkCountErrors = "";
					Localization.OverloadStrings(strings, KeyRoot + root.Name, root, ref parameterErrors, ref linkErrors, ref linkCountErrors);
					if (parameterErrors.Length + linkErrors.Length + linkCountErrors.Length > 0)
					{
						Debug.LogWarning($"{ModContext.Tag} translation {file}: entries rejected by the game (placeholders or links differ from English):\n{parameterErrors}{linkErrors}{linkCountErrors}");
					}
				}
				// The texts (already translated) go into the game's common string table, under the same keys.
				LocString.CreateLocStringKeys(root, KeyRoot);
				Debug.Log($"{ModContext.Tag} strings: language {code}, {(translated ? "translation " + file : "no translation file, English is used")}");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} strings were not localized, English is used: {e}");
			}
		}
	}
}
