# budhud-compiler

> A tool to compile away as many #base and #include directives as possible. Designed for [budhud](https://github.com/rbjaxter/budhud).

## Usage

> 💡 This tool currently only takes a single file as input. If you need to compile multiple files, consider writing a shell script that iterates over them and invokes `budhud-compiler` on each of them.

1. Grab the [latest release](https://github.com/alvancamp/budhud-compiler/releases/latest).
	- At this time, only Windows builds are provided. If you need builds for other platforms, consider building the program from source.
2. View the auto-generated help docs with `budhud-compiler.exe --help`:
	```
	budhud-compiler 1.5.0
	Copyright (C) 2022 Alex Van Camp

	  -i, --input                    Required. The specific file or directory to compile.

	  -o, --output                   (Default: ) The file or directory to output to. Prints to console if not provided. If input is a directory then output must also be a directory (or not yet exist).

	  -e, --errorOnMissing           (Default: false) If true, throws an error when a #base or #include file isn't present on disk.

	  -s, --silent                   (Default: false) If true, no information will be output to the console (aside from the finalized output if no output file is specified).

	  -m, --omitMissingDirectives    (Default: false) If true, directives which point to files that don't exist will be omitted from the final output.

	  -w, --watch                    (Default: false) If true, the input file or directory will be watched for changes and automatically recompiled to the specified output path.

	  --help                         Display this help screen.

	  --version                      Display version information.
	```
3. Run the program with the desired options.

## Compliance

This program currently uses a modified version of [ValveKeyValue](https://github.com/SteamDatabase/ValveKeyValue). You can view the source code here: https://github.com/alvancamp/ValveKeyValue/tree/dev

## License

MIT
