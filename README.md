# vscode-mono-debug-server

Standalone Mono Debug Adapter Protocol (DAP) server, forked from [microsoft/vscode-mono-debug](https://github.com/microsoft/vscode-mono-debug)

This project removes the VS Code extension layer and turns the debugger into a standalone .NET 10 CLI suitable for any DAP-capable editor (e.g. Neovim).

Licensed under MIT, same as upstream.

## Origin

* Fork of microsoft/vscode-mono-debug
* Original license: MIT
* Changes:
    * Removed VS Code extension packaging
    * Refactored into a standalone DAP server
    * Distributed as a .NET 10 CLI
    * Added Nix support (default.nix, flake-compatible)

## Installation
### From releases (recommended)

1. Go to the releases page: https://github.com/SteffenBlake/vscode-mono-debug/releases
2. Download the latest tarball for your platform
3. Extract it
4. Ensure vscode-mono-debug-server is on your PATH

Example:

```
tar -xzf vscode-mono-debug-server-<platform>.tar.gz
export PATH="$PWD/vscode-mono-debug-server:$PATH"
```

### Build from source

**Requirements:** .NET SDK 10.0+

```
git clone https://github.com/SteffenBlake/vscode-mono-debug
cd vscode-mono-debug/src/VsCodeMonoDebugServer.Console
dotnet run -- --server
```

## Nix Installation

### Option A: Flake

Use this flake snippet as an example:

```
inputs = {
  nixpkgs.url = "https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz";
  vscode-mono-debug-server.url = "github:SteffenBlake/vscode-mono-debug";
};

outputs = { self, nixpkgs, vscode-mono-debug-server }: 
let
  pkgs = nixpkgs.legacyPackages.x86_64-linux;
in {
  devShells.x86_64-linux = pkgs.mkShell {
    buildInputs = [ vscode-mono-debug-server ];
  };
}
```

To build or enter the devShell with submodules included:

```
nix build .?submodules=1#
# or
nix develop .?submodules=1#
```

---

### Option B: Direct `builtins.fetchGit`

Fetch the repository directly:

```
let
  monoDebug = builtins.fetchGit {
    url = "https://github.com/SteffenBlake/vscode-mono-debug";
    rev = "<latest-commit-hash>"; # from main
    submodules = true;
  };
in
  import monoDebug { }
```

## Neovim (nvim-dap) configuration
### Adapter setup
```
local dap = require("dap")

dap.adapters.mono = {
  type = "server",
  host = "127.0.0.1",
  port = 4711,
  executable = {
    command = "vscode-mono-debug-server",
    args = { "--server" },
  },
}

dap.configurations.cs = {
  {
    type = "mono",
    name = "Attach (Mono)",
    request = "attach",
    address = "127.0.0.1",
    port = 10000,
  },
}
```

## Debugging workflow (Android / Mono)

1. Start the server:

```
vscode-mono-debug-server --server
```

2. Connect to the device/emulator:

```
adb connect <device>
```

3. Build and deploy with debugger enabled:

```
dotnet build \
  -p:Configuration=Debug \
  "/t:Install;_Run" \
  /p:AndroidAttachDebugger=true \
  /p:AndroidSdbHostPort=10000
```

4. Open a .cs file in Neovim

5. Run:

```
:lua require("dap").continue()
```

6. Select `Attach (Mono)` when prompted

## CLI Options

```
vscode-mono-debug-server [options]
```

| Option | Description |
|------|------------|
| `--server` | Run in DAP server mode (listens on port 4711). |
| `--server=<port>` | Run in DAP server mode on the specified port. |
| `--trace` | Trace incoming DAP requests. |
| `--trace=response` | Trace incoming requests and outgoing responses. |
| `--log-file=<path>` | Write log output to the specified file. |
