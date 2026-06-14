// Exposes the tool's `internal` types (VfxGraphReflection, VfxPropertySheet, VfxControlState, …)
// to the EditMode test assembly only. No effect on production behavior.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VfxControl.Editor.Tests")]
