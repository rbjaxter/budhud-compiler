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
			Separator = ',',
			HelpText = "The specific files or directories to compile. Comma-separated.")]
		public IEnumerable<string> Inputs { get; set; } = new List<string>();

		[Option(
			'o',
			"output",
			Required = false,
			Separator = ',',
			HelpText = "The files or directories to output to. Comma-separated. Prints to console if not provided. If input is a directory then output must also be a directory (or not yet exist).")]
		public IEnumerable<string> Outputs { get; set; } = new List<string>();

		[Option(
			't',
			"trigger",
			Required = false,
			Separator = ',',
			HelpText = "A list of files or directories which, when changed, will trigger a recompile, but which themselves aren't directly recompiled. If a trigger is provided, then the entire corresponding input directory will be recompiled on every change, instead of just the file(s) that changed being recompiled. Requires --watch.")]
		public IEnumerable<string>? Triggers { get; set; }

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
			'm',
			"omitMissingDirectives",
			Required = false,
			Default = false,
			HelpText = "If true, directives which point to files that don't exist will be omitted from the final output.")]
		public bool OmitMissingDirectives { get; set; }

		[Option(
			'w',
			"watch",
			Required = false,
			Default = false,
			HelpText = "If true, the input file or directory will be watched for changes and automatically recompiled to the specified output path. Ignored if no output paths are provided.")]
		public bool Watch { get; set; }
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
		HistoryStack History = new HistoryStack();
		/// <summary>
		/// A list of #base or #include files that are missing. Keys are paths relative to the starting file, values are a flag indiciating if the directive is a #base or an #include.
		/// </summary>
		public DirectiveDict MissingDirectiveFiles = new DirectiveDict();
		/// <summary>
		/// A a dictionary of #base and #include directives discovered in every file that this loader processes. Keys are file path, values are directive type.
		/// </summary>
		public DirectiveDict DiscoveredDirectives = new DirectiveDict();

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
			return Program.LowercasifyStream(File.ReadAllText(resolvedFilePath));
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
		/// Used to extract keys whose values are objects. Supports keys with conditionals after them.
		/// </summary>
		static Regex objectKeyRx = new Regex(@"(""?)(\w+)(""?\s*(?:\[.*\])*(?:\/\/.*\n)?\s*{)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		/// <summary>
		/// Used to convert "\" to "/" in directive file paths.
		/// </summary>
		static Regex backslashRx = new Regex(@"\\", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		static int TAB_SIZE = 4;
		static int VALUE_COLUMN = 68;
		static Dictionary<string, CancellationTokenSource> PathsAwaitingProcessing = new Dictionary<string, CancellationTokenSource>();
		/// <summary>
		/// Used to prevent garbage collection of FileSystemWatchers.
		/// </summary>
		static HashSet<FileSystemWatcher> Watchers = new HashSet<FileSystemWatcher>();

		static void Main(string[] args)
		{
			var options = Parser.Default.ParseArguments<Options>(args).Value;
			var numInputs = options.Inputs.Count();
			var numOutputs = options.Inputs.Count();
			var outputToConsole = OutputToConsole(options);

			if (!outputToConsole && numInputs != numOutputs)
			{
				throw new ArgumentException("The number of outputs must match the number of inputs (unless outputting to console).");
			}

			for (var i = 0; i < numInputs; i++)
			{
				var input = options.Inputs.ElementAt(i);
				var output = outputToConsole ? null : options.Outputs.ElementAt(i);

				CompileOrCopyFiles(EnumerateInputFiles(input, options), input, output, options);

				if (outputToConsole)
				{
					continue;
				}

				if (output == null) throw new InvalidOperationException("Unexpected null value");

				var inputIsDir = InputIsDir(input, output, options);
				if (options.Watch)
				{
					string dirToWatch;
					var watchingSingleFile = false;
					var trigger = options.Triggers == null ? null : options.Triggers.ElementAt(i);

					// If the user hasn't specified a trigger directory or file for this input, then just watch the input itself for changes.
					// Else, watch the trigger directory/file.
					if (String.IsNullOrEmpty(trigger))
					{
						var tmp = inputIsDir ? input : Path.GetDirectoryName(input);
						if (String.IsNullOrEmpty(tmp))
						{
							throw new InvalidDataException($"Could not extract directory name from {input}");
						}
						dirToWatch = tmp;
						watchingSingleFile = !inputIsDir;
					}
					else
					{
						dirToWatch = trigger;
						watchingSingleFile = !Directory.Exists(trigger);
					}

					var watcher = new FileSystemWatcher(dirToWatch);
					watcher.NotifyFilter = NotifyFilters.DirectoryName
									 | NotifyFilters.FileName
									 | NotifyFilters.LastWrite;

					if (watchingSingleFile)
					{
						watcher.Filter = Path.GetFileName(input);
					}
					else
					{
						watcher.IncludeSubdirectories = true;
					}

					// If we're not using a trigger, then we can get away with only recompiling the file(s) that changed.
					// Else, we have to blindly recompile the entire input folder on every change.
					if (String.IsNullOrEmpty(trigger))
					{
						watcher.Changed += (object sender, FileSystemEventArgs e) => HandleNormalWatcherCallback("CHANGED", e.FullPath, input, output, options);
						watcher.Created += (object sender, FileSystemEventArgs e) => HandleNormalWatcherCallback("CREATED", e.FullPath, input, output, options);
						watcher.Renamed += (object sender, RenamedEventArgs e) => HandleNormalWatcherCallback("RENAMED", e.FullPath, input, output, options);
					}
					else
					{
						watcher.Changed += (object sender, FileSystemEventArgs e) => HandleTriggerWatcherCallback(trigger, input, output, options);
						watcher.Created += (object sender, FileSystemEventArgs e) => HandleTriggerWatcherCallback(trigger, input, output, options);
						watcher.Renamed += (object sender, RenamedEventArgs e) => HandleTriggerWatcherCallback(trigger, input, output, options);
					}

					watcher.Error += (object sender, ErrorEventArgs e) => throw e.GetException();
					watcher.EnableRaisingEvents = true;
					Watchers.Add(watcher); // Prevent garbage collection

					if (String.IsNullOrEmpty(trigger))
					{
						Console.WriteLine($"Initial compilation complete, watching \"{input}\" for changes, will output to \"{output}\"...");
					}
					else
					{
						Console.WriteLine($"Initial compilation complete, watching trigger \"{trigger}\" for changes which will compile \"{input}\" to \"{output}\"...");
					}
				}
			}

			if (options.Watch)
			{
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
			}
		}
		static void HandleNormalWatcherCallback(string operation, string filePath, string input, string output, Options options)
		{
			// Cancel the existing task if it exists.
			PathsAwaitingProcessing.TryGetValue(filePath, out CancellationTokenSource? existingCancellationToken);
			if (existingCancellationToken != null)
			{
				existingCancellationToken.Cancel();
				PathsAwaitingProcessing.Remove(filePath);
			}

			// Create a new task
			CancellationTokenSource source = new CancellationTokenSource();
			var t = Task.Run(async delegate
					{
						await Task.Delay(100, source.Token);
						CompileOrCopyFiles(new List<string> { filePath }, input, output, options);
						PathsAwaitingProcessing.Remove(filePath);
						Console.WriteLine($"{operation} | {(ShouldCompile(filePath) ? "Compiled" : "Copied")} \"{filePath}\" to \"{ComputeOutputPath(filePath, input, output)}\"");
					});
			PathsAwaitingProcessing.Add(filePath, source);
		}

		static void HandleTriggerWatcherCallback(string trigger, string input, string output, Options options)
		{
			// Cancel the existing task if it exists.
			PathsAwaitingProcessing.TryGetValue(trigger, out CancellationTokenSource? existingCancellationToken);
			if (existingCancellationToken != null)
			{
				existingCancellationToken.Cancel();
				PathsAwaitingProcessing.Remove(trigger);
			}

			// Create a new task
			CancellationTokenSource source = new CancellationTokenSource();
			var t = Task.Run(async delegate
					{
						await Task.Delay(100, source.Token);
						Console.WriteLine($"Trigger \"{trigger}\" changed, compiling \"{input}\" to \"{output}\"...");
						CompileOrCopyFiles(EnumerateInputFiles(input, options), input, output, options);
						PathsAwaitingProcessing.Remove(trigger);
						Console.WriteLine($"Compiled \"{input}\" to \"{output}\"");
					});
			PathsAwaitingProcessing.Add(trigger, source);
		}

		static bool OutputToConsole(Options options)
		{
			return options.Outputs.Count() <= 0;
		}

		static bool InputIsDir(string input, string output, Options options)
		{
			var outputToConsole = OutputToConsole(options);
			if (!File.Exists(input))
			{
				if (Directory.Exists(input))
				{
					if (!outputToConsole && !Directory.Exists(output) && File.Exists(output))
					{
						throw new ArgumentException("Output path already exists and is a file. Because input path is a directory, output path must either also be a directory or not yet exist.");
					}
					return true;
				}
				else
				{
					throw new ArgumentException("Input path does not exist.");
				}
			}
			return false;
		}

		static IEnumerable<string> EnumerateInputFiles(string input, Options options)
		{
			IEnumerable<string> files;
			if (Directory.Exists(input))
			{
				files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories);
			}
			else
			{
				files = new List<string> { input };
			}
			return files;
		}

		static string ComputeOutputPath(string filePath, string input, string output)
		{
			var relativeInputPath = Regex.Replace(filePath.Replace(input, ""), @"^[\\/]+", "");
			return Path.Combine(output, relativeInputPath);
		}

		static void CompileOrCopyFiles(IEnumerable<string> files, string input, string? output, Options options)
		{
			var outputToConsole = output == null;
			foreach (var f in files)
			{
				if (ShouldCompile(f))
				{
					var outputPath = output == null ? null : ComputeOutputPath(f, input, output);
					var result = Compile(f, outputPath, options);
					if (outputPath == null)
					{
						Console.Write($"\n{result}");
					}
					else
					{
						if (output == null) throw new InvalidOperationException("Unexpected null value");
						var outputDir = Path.GetDirectoryName(outputPath);
						if (!String.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
						{
							Directory.CreateDirectory(outputDir);
						}
						File.WriteAllText(outputPath, result);
					}
				}
				else if (!outputToConsole)
				{
					if (output == null) throw new InvalidOperationException("Unexpected null value");
					var outputPath = ComputeOutputPath(f, input, output);
					var outputDir = Path.GetDirectoryName(outputPath);
					if (!String.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
					{
						Directory.CreateDirectory(outputDir);
					}

					try
					{
						File.Copy(f, outputPath, true);
					}
					catch (IOException ex)
					{
						// TF2 locks font files while the game is open, which means that compiling the HUD while the game is running won't work.
						// To work around this, we detect IOExceptions on font files and just ignore them.
						if (Path.GetExtension(f) == ".ttf" && Regex.IsMatch(ex.Message, @"because it is being used by another process\.$"))
						{
							if (!options.Silent)
							{
								Console.WriteLine($"Font file {f} locked, can't overwrite. Skipping.");
							}
						}
						else
						{
							throw;
						}
					}
				}
			}
		}

		static string Compile(string inputPath, string? outputPath, Options options)
		{
			// If the input doesn't point to a fully qualified (aka absolute) path, make it do so.
			var absInputPath = inputPath;
			if (!Path.IsPathFullyQualified(absInputPath))
			{
				absInputPath = Path.Combine(Directory.GetCurrentDirectory(), absInputPath);
			}

			// Do the same for the output.
			var absOutputPath = outputPath;
			if (absOutputPath != null && !Path.IsPathFullyQualified(absOutputPath))
			{
				absOutputPath = Path.Combine(Directory.GetCurrentDirectory(), absOutputPath);
			}

			if (!options.Silent)
			{
				Console.WriteLine($"Entry point: {absInputPath}");
			}

			var inputDirName = FileLoader.GetDirectoryName(absInputPath);
			var output = "";
			var fullText = File.ReadAllText(absInputPath);
			var allDirectives = ListDirectives(fullText);
			DirectiveDict missingDirectiveFiles = new DirectiveDict();
			var inputStream = LowercasifyStream(File.ReadAllText(absInputPath));
			var fileLoader = new FileLoader(absInputPath, options.ErrorOnMissing, options.Silent);
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
				var directivePath = Path.Combine(inputDirName, directive.Key);
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
					string newKey;

					// If no output path was provided, then we are outputting to console.
					// If that's the case, then we can't resolve the missing directives
					// relative to the output path, so we just output them unchanged.
					// Else, we have to rewrite the directive paths to be relative
					// to the output file instead of the input file.
					if (absOutputPath == null)
					{
						newKey = directive + " \"" + backslashRx.Replace(missingFile.Key, "/") + "\"";
					}
					else
					{
						var outputDirName = FileLoader.GetDirectoryName(absOutputPath);
						var absDirectivePath = Path.GetFullPath(Path.Combine(inputDirName, missingFile.Key));
						var outputRelativePath = Path.GetRelativePath(outputDirName, absDirectivePath);
						newKey = directive + " \"" + backslashRx.Replace(outputRelativePath, "/") + "\"";
					}
					output = String.Concat(output, $"{newKey}\n");
				}
			}

			output = String.Concat(output, StringifyKVObject(data));
			return output;
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
			DirectiveDict output = new DirectiveDict();
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

		/// <summary>
		/// So it turns out that Valve's KV implementation is case-insensitive on object keys, but VKV is case-sensitive.
		/// This resulted in scenarios where things like "NumberBG" and "NumberBg" would collide in budhud's compiled output.
		/// To resolve this, we just convert all object keys to lowercase.
		/// </summary>
		/// <returns>A stream that is the same as the input stream but with all characters lowercase.</returns>
		public static Stream LowercasifyStream(string input)
		{
			string lowercasedStr = objectKeyRx.Replace(input, m => m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant() + m.Groups[3].Value);
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

		public static bool ShouldCompile(string input)
		{
			return input.EndsWith(".vdf") || input.EndsWith(".res") || input.EndsWith("hudanimations_manifest.txt") || input.EndsWith("mod_textures.txt");
		}
	}
}
