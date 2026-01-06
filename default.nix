{
  lib,
  buildDotnetModule, 
  dotnetCorePackages,
}:

buildDotnetModule rec {
  pname = "vscode-mono-debug-server";
  version = "0.16.3-s";

  src = ./.;

  # Point to your main project
  projectFile = "src/VsCodeMonoDebugServer.Console/VsCodeMonoDebugServer.Console.csproj";

  # Start with empty deps.json; generate later if needed
  nugetDeps = ./deps.json;

  # SDK and runtime
  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;

  # Single-file self-contained executable
  selfContainedBuild = true;
  useAppHost = true;

  # The output executable name
  executables = [ "vscode-mono-debug-server" ];

  # No additional runtime deps
  runtimeDeps = [];

  # Optional: Release build
  buildType = "Release";

  # Metadata
  meta = with lib; {
    description = "Language Server Protocol (LSP) server for debugging Mono applications";
    homepage = "https://github.com/SteffenBlake/vscode-mono-debug-server";
    license = licenses.mit;
    maintainers = [ maintainers.SteffenBlake ];
  };
}
