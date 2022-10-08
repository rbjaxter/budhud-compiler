using System.Text;
using System.Text.RegularExpressions;
using CommandLine;
using ValveKeyValue;

namespace BudhudCompiler
{
	using DirectiveDict = Dictionary<string, DirectiveType>;
	using HistoryStack = Stack<(string dirName, string filePath, string contents)>;
	enum DirectiveType
	{
		BASE,
		INCLUDE
	}

	/// <summary>
	/// Command-line options used by the CommandLineParser library.
	/// </summary>
	class Options
	{
		[Option(
			'i',
			"input",
			Required = true,
			HelpText = "The specific file to compile.")]
		public string Input { get; set; } = "";

		[Option(
			'o',
			"output",
			Required = false,
			Default = "",
			HelpText = "The file to output to. Prints to console if not provided.")]
		public string Output { get; set; } = "";

		[Option(
			'e',
			"errorOnMissing",
			Required = false,
			Default = false,
			HelpText = "If true, throws an error when a #base or #include file isn't present on disk.")]
		public bool ErrorOnMissing { get; set; }

		[Option(
			's',
			"silent",
			Required = false,
			Default = false,
			HelpText = "If true, no information will be output to the console (aside from the finalized output if no output file is specified).")]
		public bool Silent { get; set; }


		[Option(
			'o',
			"omitMissingDirectives",
			Required = false,
			Default = false,
			HelpText = "If true, directives which point to files that don't exist will be omitted from the final output.")]
		public bool OmitMissingDirectives { get; set; }
	}

	/// <summary>
	/// Consumed by ValveKeyValue to handle loading of #base and #include files.
	/// </summary>
	class FileLoader : IIncludedFileLoader
	{
		bool SkipMissingFiles;
		bool Silent;
		string StartingFilePath;
		/// <summary>
		/// A trail of breadcrumbs that tells us how far into a chain of directives we are. The first entry is the starting file. This is used to resolve relative directive paths into absolute paths via a heuristic.
		/// </summary>
		HistoryStack History = new Stack<(string dirName, string filePath, string contents)>();
		/// <summary>
		/// A list of #base or #include files that are missing. Keys are paths relative to the starting file, values are a flag indiciating if the directive is a #base or an #include.
		/// </summary>
		public DirectiveDict MissingDirectiveFiles = new Dictionary<string, DirectiveType>();
		/// <summary>
		/// A a dictionary of #base and #include directives discovered in every file that this loader processes. Keys are file path, values are directive type.
		/// </summary>
		public DirectiveDict DiscoveredDirectives = new Dictionary<string, DirectiveType>();

		public FileLoader(string startingFilePath, bool errorOnMissing, bool silent)
		{
			StartingFilePath = startingFilePath;
			SkipMissingFiles = !errorOnMissing;
			Silent = silent;
			History.Push
			(
				(
					dirName: GetDirectoryName(startingFilePath),
					filePath: startingFilePath,
					contents: File.ReadAllText(startingFilePath)
				)
			);
		}

		Stream IIncludedFileLoader.OpenFile(string filePath)
		{
			string resolvedFilePath = "";

			// Use the History stack to figure out which file we are currently in
			// and then use that information to resolve this directive's relative path
			// into an absolute one.
			var foundInLastFile = false;
			var done = false;
			while (!foundInLastFile && !done)
			{
				var rx = new Regex(@"(^\s*(?:#base|#include)\s*""" + Regex.Escape(filePath) + @"\"")", RegexOptions.IgnoreCase | RegexOptions.Multiline);
				var latestHistoryEntry = History.Peek();
				resolvedFilePath = Path.GetFullPath(Path.Combine(latestHistoryEntry.dirName, filePath));
				foundInLastFile = rx.IsMatch(latestHistoryEntry.contents);
				if (foundInLastFile)
				{
					if (File.Exists(resolvedFilePath))
					{
						var dirName = GetDirectoryName(resolvedFilePath);
						var contents = File.ReadAllText(resolvedFilePath);
						var contentsDirectives = Program.ListDirectives(contents);

						foreach (var directive in contentsDirectives)
						{
							var resolvedDirectivePath = Path.Combine(dirName, directive.Key);
							if (!File.Exists(resolvedDirectivePath))
							{
								var directivePathRelativeToStartingFile = Path.GetRelativePath(History.ElementAt(0).dirName, resolvedDirectivePath);
								directivePathRelativeToStartingFile = directivePathRelativeToStartingFile.Replace("\\", "/");
								MissingDirectiveFiles.Add(directivePathRelativeToStartingFile, directive.Value);
							}
						}

						History.Push
						(
							(
								dirName,
								filePath: resolvedFilePath,
								contents
							)
						);
					}
				}
				else if (History.Count > 1)
				{
					History.Pop();
				}
				else
				{
					done = true;
				}
			}

			if (String.IsNullOrEmpty(resolvedFilePath) || !File.Exists(resolvedFilePath))
			{
				if (!Silent)
				{
					Console.WriteLine($"Skipping non-existent #base or #include: {resolvedFilePath}");
				}
				return Stream.Null;
			}

			if (!Silent)
			{
				Console.WriteLine($"Processing #base or #include: {resolvedFilePath}");
			}

			// Parse the file to add its directives to the DiscoveredDirectives dictionary.
			var fullText = File.ReadAllText(resolvedFilePath);
			var directives = Program.ListDirectives(fullText);
			foreach (var directive in directives)
			{
				if (!DiscoveredDirectives.ContainsKey(directive.Key))
				{
					DiscoveredDirectives.Add(directive.Key, directive.Value);
				}
			}

			// Open the file and return the stream to VKV.
			return Program.LowercasifyStream(File.OpenRead(resolvedFilePath));
		}

		public static string GetDirectoryName(string filePath)
		{
			var dirName = Path.GetDirectoryName(filePath);
			if (dirName == null)
			{
				throw new InvalidDataException($"Could not extract directory name from current file \"{filePath}\". Is it a valid file path?");
			}
			return dirName;
		}

		int GetPathDepth(string filePath)
		{
			return Path.GetFullPath(filePath).Split(Path.DirectorySeparatorChar, System.StringSplitOptions.RemoveEmptyEntries).Length;
		}
	}

	class Program
	{
		/// <summary>
		/// Used to extract all #base and #include directives from a file.
		/// </summary>
		static Regex directiveRx = new Regex(@"(^\s*(?:#base|#include)\s*""(.+)"")", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		/// <summary>
		/// Used extract keys whose values are objects. Supports keys with conditionals after them.
		/// </summary>
		static Regex objectKeyRx = new Regex(@"(""?)(\w+)(""?\s+(?:\[.+\])*\s+{)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		static int TAB_SIZE = 4;
		static int VALUE_COLUMN = 68;

		static void Main(string[] args)
		{
			// Parse the command-line arguments and then do things with those parsed args.
			Parser.Default.ParseArguments<Options>(args).WithParsed(options =>
			{
				// If the input doesn't point to a fully qualified (aka absolute) path, make it do so.
				var inputFilePath = options.Input;
				if (!Path.IsPathFullyQualified(inputFilePath))
				{
					inputFilePath = Path.Combine(Directory.GetCurrentDirectory(), inputFilePath);
				}

				if (!options.Silent)
				{
					Console.WriteLine($"Entry point: {inputFilePath}");
				}

				var inputFileDir = FileLoader.GetDirectoryName(inputFilePath);
				var output = "";
				var fullText = File.ReadAllText(inputFilePath);
				var allDirectives = ListDirectives(fullText);
				DirectiveDict missingDirectiveFiles = new Dictionary<string, DirectiveType>();
				var inputStream = LowercasifyStream(File.OpenRead(inputFilePath));
				var fileLoader = new FileLoader(inputFilePath, options.ErrorOnMissing, options.Silent);
				var serializerOptions = new KVSerializerOptions
				{
					FileLoader = fileLoader,
				};
				serializerOptions.Conditions.Add("WIN32");

				var kv = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
				KVObject data = kv.Deserialize(inputStream, serializerOptions);

				// Catalog missing directives in the input file.
				foreach (var directive in allDirectives)
				{
					var directivePath = Path.Combine(inputFileDir, directive.Key);
					if (!File.Exists(directivePath))
					{
						missingDirectiveFiles.Add(directive.Key, directive.Value);
					}
				}

				// Catalog any directives that the file loader discovered, avoiding duplicates.
				foreach (var directive in fileLoader.DiscoveredDirectives)
				{
					if (!allDirectives.ContainsKey(directive.Key))
					{
						allDirectives.Add(directive.Key, directive.Value);
					}
				}
				foreach (var missingFile in fileLoader.MissingDirectiveFiles)
				{
					if (!missingDirectiveFiles.ContainsKey(missingFile.Key))
					{
						missingDirectiveFiles.Add(missingFile.Key, missingFile.Value);
					}
				}

				if (!options.OmitMissingDirectives)
				{
					// Add missing directives to output.
					foreach (var missingFile in missingDirectiveFiles)
					{
						var directive = DirectiveTypeToDirectiveString(missingFile.Value);
						var newKey = directive + " \"" + missingFile.Key + "\"";
						output = String.Concat(output, $"{newKey}\n");
					}
				}

				output = String.Concat(output, StringifyKVObject(data));

				if (!String.IsNullOrEmpty(options.Output))
				{
					File.WriteAllText(options.Output, output);
				}
				else
				{
					Console.Write($"\n{output}");
				}
			});
		}

		/// <summary>
		/// Recursively stringifies (and pretty-prints) a Valve KVObject.
		/// </summary>
		static string StringifyKVObject(KVObject input, int indentationLevel = 0)
		{
			var result = GenerateTabs(indentationLevel);

			if (IsEnumerableType(input.Value.GetType()))
			{
				result = String.Concat('\n' + result, $"\"{input.Name}\"\n");
				result = String.Concat(result, GenerateTabs(indentationLevel));
				result = String.Concat(result, "{\n");
				foreach (KVObject item in input)
				{
					var subResult = StringifyKVObject(item, indentationLevel + 1);
					result = String.Concat(result, subResult);
				}
				result = String.Concat(result, GenerateTabs(indentationLevel));
				result = String.Concat(result, "}\n");
			}
			else
			{
				string nameWithQuotes = $"\"{input.Name}\"";
				string valueWithQuotes = $"\"{input.Value}\"";
				int column = indentationLevel * TAB_SIZE;
				column += nameWithQuotes.Length;
				string spacesToValue = " ";
				if (column < VALUE_COLUMN)
				{
					spacesToValue = new string(' ', VALUE_COLUMN - column);
				}
				result = String.Concat(result, $"{nameWithQuotes}{spacesToValue}{valueWithQuotes}\n");
			}

			return result;
		}

		static bool IsEnumerableType(Type type)
		{
			return (type.Name != nameof(String)
				&& type.GetInterface(nameof(IEnumerable<object>)) != null);
		}

		/// <summary>
		/// Generates indentation as spaces.
		/// </summary>
		static string GenerateTabs(int num)
		{
			var result = "";
			for (int i = 0; i < num; i++)
			{
				result = String.Concat(result, new string(' ', TAB_SIZE));
			}
			return result;
		}

		/// <returns>A map of all directives in the input string. Keys are the full directive statement, values are a tuple containing the filePath and the type.</returns>
		public static DirectiveDict ListDirectives(string input)
		{
			DirectiveDict output = new Dictionary<string, DirectiveType>();
			var directiveMatches = directiveRx.Matches(input);

			foreach (Match match in directiveMatches)
			{
				var groups = match.Groups;
				var type = DirectiveStringToDirectiveType(groups[1].ToString());
				output.Add(groups[2].ToString(), type);
			}

			return output;
		}

		static Stream StringToStream(string input)
		{
			byte[] byteArray = Encoding.UTF8.GetBytes(input);
			return new MemoryStream(byteArray);
		}

		static String StreamToString(Stream input)
		{
			StreamReader reader = new StreamReader(input);
			return reader.ReadToEnd();
		}

		/// <summary>
		/// So it turns out that Valve's KV implementation is case-insensitive on object keys, but VKV is case-sensitive.
		/// This resulted in scenarios where things like "NumberBG" and "NumberBg" would collide in budhud's compiled output.
		/// To resolve this, we just convert all object keys to lowercase.
		/// </summary>
		/// <returns>A stream that is the same as the input stream but with all characters lowercase.</returns>
		public static Stream LowercasifyStream(Stream input)
		{
			string inputStr = StreamToString(input);
			string lowercasedStr = objectKeyRx.Replace(inputStr, m => m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant() + m.Groups[3].Value);
			return StringToStream(lowercasedStr);
		}

		public static string DirectiveTypeToDirectiveString(DirectiveType input)
		{
			string directive;
			if (input == DirectiveType.BASE)
			{
				directive = "#base";
			}
			else if (input == DirectiveType.INCLUDE)
			{
				directive = "#include";
			}
			else
			{
				throw new InvalidDataException("Invalid directive type: " + input);
			}
			return directive;
		}

		public static DirectiveType DirectiveStringToDirectiveType(string input)
		{
			DirectiveType type;
			if (Regex.IsMatch(input, @"\s*#base\s*"""))
			{
				type = DirectiveType.BASE;
			}
			else if (Regex.IsMatch(input, @"\s*#include\s*"""))
			{
				type = DirectiveType.INCLUDE;
			}
			else
			{
				throw new InvalidDataException("Encountered a directive of an unknown type: " + input);
			}
			return type;
		}
	}
}
