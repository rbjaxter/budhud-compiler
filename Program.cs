using System.Text.RegularExpressions;
using CommandLine;
using ValveKeyValue;

namespace BudhudCompiler
{
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
			HelpText = "If false, throws an error when a #base file isn't present on disk.")]
		public bool SkipMissingFiles { get; set; }

		[Option(
			's',
			"silent",
			Required = false,
			Default = false,
			HelpText = "If true, no information will be output to the console (aside from the finalized output if no output file is specified).")]
		public bool Silent { get; set; }
	}

	class FileLoader : IIncludedFileLoader
	{
		string BasePath;
		bool SkipMissingFiles;
		bool Silent;
		/// <summary>
		/// A list of #base or #include files that are missing, but at this time we don't know for sure if they are #base or #include directives. That gets figured out later.
		/// </summary>
		public List<string> MissingDirectiveFiles = new List<string>();
		public Dictionary<string, string> DiscoveredDirectives = new Dictionary<string, string>();

		public FileLoader(string basePath, bool skipMissingFiles, bool silent)
		{
			this.BasePath = basePath;
			this.SkipMissingFiles = skipMissingFiles;
			this.Silent = silent;
		}

		Stream IIncludedFileLoader.OpenFile(string filePath)
		{
			var combinedPath = Path.Combine(this.BasePath, filePath);
			var resolvedPath = Path.GetFullPath(combinedPath);
			if (File.Exists(resolvedPath))
			{
				if (!Silent)
				{
					Console.WriteLine($"Processing #base or #include: {resolvedPath}");
				}

				var fullText = File.ReadAllText(resolvedPath);
				var directives = Program.ListDirectives(fullText);
				DiscoveredDirectives = DiscoveredDirectives.Concat(directives)
					   .ToDictionary(x => x.Key, x => x.Value);

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
	}

	class Program
	{
		static Regex directiveRx = new Regex(@"(^\s*(?:#base|#include).+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

		static void Main(string[] args)
		{
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
				var dirName = Path.GetDirectoryName(options.Input);

				if (dirName == null)
				{
					throw new Exception("Could not extract directory name from --filename. Is it a valid file path?");
				}

				var fileLoader = new FileLoader(dirName, options.SkipMissingFiles, options.Silent);
				var serializerOptions = new KVSerializerOptions
				{
					FileLoader = fileLoader,
				};
				serializerOptions.Conditions.Add("WIN32");

				var kv = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
				KVObject data = kv.Deserialize(inputStream, serializerOptions);

				// Catalog any directives that the file loader discovered.
				allDirectives = allDirectives.Concat(fileLoader.DiscoveredDirectives)
					   .ToDictionary(x => x.Key, x => x.Value);
				missingDirectiveFiles.AddRange(fileLoader.MissingDirectiveFiles);

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

		static string GenerateTabs(int num)
		{
			var result = "";
			for (int i = 0; i < num; i++)
			{
				result = String.Concat(result, '\t');
			}
			return result;
		}

		/// <returns>A map of all directives in the input string. Keys are the full directive statement, values are the file path only.</returns>
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
