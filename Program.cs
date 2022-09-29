using System.Text.RegularExpressions;
using CommandLine;
using ValveKeyValue;

namespace BudhudCompiler
{
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
			'm',
			"skipMissingFiles",
			Required = false,
			Default = true,
			HelpText = "If false, throws an error when a #base or #include file isn't present on disk.")]
		public bool SkipMissingFiles { get; set; }

		[Option(
			's',
			"silent",
			Required = false,
			Default = false,
			HelpText = "If true, no information will be output to the console (aside from the finalized output if no output file is specified).")]
		public bool Silent { get; set; }
	}

	/// <summary>
	/// Consumed by ValveKeyValue to handle loading of #base and #include files.
	/// </summary>
	class FileLoader : IIncludedFileLoader
	{
		bool SkipMissingFiles;
		bool Silent;
		Stack<(string dirName, string filePath, string contents)> History = new Stack<(string dirName, string filePath, string contents)>();
		/// <summary>
		/// A list of #base or #include files that are missing, but at this time we don't know for sure if they are #base or #include directives. That gets figured out later.
		/// </summary>
		public List<string> MissingDirectiveFiles = new List<string>();
		/// <summary>
		/// A a dictionary of #base and #include directives discovered in every file that this loader processes. Keys are the full directive string, values are just the filename without quotes.
		/// </summary>
		public Dictionary<string, string> DiscoveredDirectives = new Dictionary<string, string>();

		public FileLoader(string startingFile, bool skipMissingFiles, bool silent)
		{
			SkipMissingFiles = skipMissingFiles;
			Silent = silent;
			History.Push
			(
				(
					dirName: GetDirectoryName(startingFile), 
					filePath: startingFile, 
					contents: File.ReadAllText(startingFile)
				)
			);
		}

		Stream IIncludedFileLoader.OpenFile(string filePath)
		{
			var combinedPath = Path.Combine(History.Peek().dirName, filePath);
			var resolvedPath = Path.GetFullPath(combinedPath);
			if (File.Exists(resolvedPath))
			{
				if (!Silent)
				{
					Console.WriteLine($"Processing #base or #include: {resolvedPath}");
				}

				// Do all this bullshit.
				var foundInLastFile = false;
				var done = false;
				while (!foundInLastFile && !done)
				{
					var rx = new Regex(@"(^\s*(?:#base|#include)\s*""" + Regex.Escape(filePath) + @"\"")", RegexOptions.IgnoreCase | RegexOptions.Multiline);
					var latestHistoryEntry = History.Peek();
					var resolvedFilePath = Path.GetFullPath(Path.Combine(latestHistoryEntry.dirName, filePath));
					if (File.Exists(resolvedFilePath))
					{
						foundInLastFile = rx.IsMatch(latestHistoryEntry.contents);
						if (foundInLastFile)
						{
							History.Push
							(
								(
									dirName: GetDirectoryName(resolvedFilePath),
									filePath: resolvedFilePath,
									contents: File.ReadAllText(resolvedFilePath)
								)
							);
						}
						else
						{
							History.Pop();
						}
					}
					else
					{
						done = true;
					}
				}

				// Parse the file to add its directives to the DiscoveredDirectives dictionary.
				var fullText = File.ReadAllText(resolvedPath);
				var directives = Program.ListDirectives(fullText);
				foreach (var directive in directives)
				{
					if (!DiscoveredDirectives.ContainsKey(directive.Key))
					{
						DiscoveredDirectives.Add(directive.Key, directive.Value);
					}
				}

				// Open the file and return the stream to VKV.
				var stream = File.OpenRead(resolvedPath);
				return stream;
			}
			else if (!this.SkipMissingFiles)
			{
				throw new FileNotFoundException("Resource not found.", filePath);
			}

			if (!Silent)
			{
				Console.WriteLine($"Skipping non-existent #base or #include: {resolvedPath}");
			}

			MissingDirectiveFiles.Add(filePath);
			return Stream.Null;
		}

		string GetDirectoryName(string filePath)
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
		static Regex directiveRx = new Regex(@"(^\s*(?:#base|#include).+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

		static void Main(string[] args)
		{
			// Parse the command-line arguments and then do things with those parsed args.
			Parser.Default.ParseArguments<Options>(args).WithParsed(options =>
			{
				if (!options.Silent)
				{
					Console.WriteLine($"Entry point: {options.Input}");
				}

				var output = "";
				var fullText = File.ReadAllText(options.Input);
				var allDirectives = ListDirectives(fullText);
				var missingDirectiveFiles = new List<string>();
				var inputStream = File.OpenRead(options.Input);
				var fileLoader = new FileLoader(options.Input, options.SkipMissingFiles, options.Silent);
				var serializerOptions = new KVSerializerOptions
				{
					FileLoader = fileLoader,
				};
				serializerOptions.Conditions.Add("WIN32");

				var kv = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
				KVObject data = kv.Deserialize(inputStream, serializerOptions);

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
					if (!missingDirectiveFiles.Contains(missingFile))
					{
						missingDirectiveFiles.Add(missingFile);
					}
				}

				// Figure out which directives point to missing files and add those directives to the output.
				var addedMissingDirectiveToOutput = false;
				foreach (var missingFile in missingDirectiveFiles)
				{
					foreach (var directive in allDirectives)
					{
						if (directive.Key.Contains(missingFile))
						{
							output = String.Concat(output, $"{directive.Key.Trim()}\n");
							addedMissingDirectiveToOutput = true;
						}
					}
				}

				// For a bit of pretty-printing, we add a newline after the directives, if any were left in the file.
				if (addedMissingDirectiveToOutput)
				{
					output = String.Concat(output, "\n");
				}

				output = String.Concat(output, StringifyKVObject(data));

				if (options.Output != "")
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
				result = String.Concat(result, $"\"{input.Name}\"\n");
				result = String.Concat(result, GenerateTabs(indentationLevel));
				result = String.Concat(result, "{\n");
				foreach (KVObject item in input)
				{
					var subResult = StringifyKVObject(item, indentationLevel + 1);
					result = String.Concat(result, subResult);
				}
				result = String.Concat(result, GenerateTabs(indentationLevel));
				result = String.Concat(result, "}\n\n");
			}
			else
			{
				result = String.Concat(result, $"\"{input.Name}\"\t\"{input.Value}\"\n");
			}

			return result;
		}

		static bool IsEnumerableType(Type type)
		{
			return (type.Name != nameof(String)
				&& type.GetInterface(nameof(IEnumerable<object>)) != null);
		}

		/// <summary>
		/// Generates a string of tab characters.
		/// </summary>
		static string GenerateTabs(int num)
		{
			var result = "";
			for (int i = 0; i < num; i++)
			{
				result = String.Concat(result, '\t');
			}
			return result;
		}

		/// <returns>A map of all directives in the input string. Keys are the full directive statement, values are the file path only (without quotes).</returns>
		public static Dictionary<string, string> ListDirectives(string input)
		{
			var output = new Dictionary<string, string>();
			var directiveMatches = directiveRx.Matches(input);

			foreach (Match match in directiveMatches)
			{
				var groups = match.Groups;
				output.Add(groups[0].ToString(), groups[1].ToString());
			}

			return output;
		}
	}
}
