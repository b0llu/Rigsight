#if RIGSIGHT_APP
// Linked into the app, which draws widgets with the same code for its Widgets page (see Rigsight.csproj).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>A game's frame rate (per second), last frame time and 1% low, as measured by RivaTuner.</summary>
internal readonly record struct FrameStats(double Fps, double FrameTimeMs, double? OnePercentLow);
