# Canvas UI across projects

Canvas UI was requested during HeyBuddy's implementation. Its registry is for web source components, while HeyBuddy is a native WPF app. HeyBuddy's companion uses original native vector drawing and motion; no Canvas UI JavaScript runtime or copied component port is embedded.

Shared tooling is installed under `<shared-tools>\shadcn`, pinned to shadcn 4.20.1, using the configured shared npm cache. Codex's global shadcn MCP entry and the global `canvas-ui` skill make registry discovery and installation reusable across projects after a Codex reload.

From an initialized web project's folder:

```powershell
node '<shared-tools>\shadcn\node_modules\shadcn\dist\index.js' add @canvas-ui/liquid-react
```

Use the component's matching framework variant. Preserve existing `components.json`; add the `@canvas-ui` registry only if needed. Components are fetched from upstream for each project rather than repackaged into a shared copied library.

Sources: [Canvas UI installation](https://canvasui.dev/docs/installation), [MCP](https://canvasui.dev/docs/mcp), [rendering fallbacks](https://canvasui.dev/docs/rendering), [license](https://github.com/DavidHDev/canvas-ui/blob/main/LICENSE.md).
