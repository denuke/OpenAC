using AcDream.App.Rendering;
using AcDream.App.Update;
using AcDream.Core.Selection;

namespace AcDream.App.Navigation;

/// <summary>
/// Advances navigation walks every update frame, before gameplay input is read,
/// and carries out the navigation debug keys: route to the selection, and walk
/// to the selection or stop the walk under way.
/// </summary>
internal sealed class NavigationWalkFramePhase : IGameplayInputFramePhase
{
    private readonly NavigationWalkController _walk;
    private readonly WorldSceneDebugState _debug;
    private readonly SelectionState _selection;
    private readonly IGameplayInputFramePhase _input;
    private int _routeRequestsSeen;
    private int _walkRequestsSeen;

    public NavigationWalkFramePhase(
        NavigationWalkController walk,
        WorldSceneDebugState debug,
        SelectionState selection,
        IGameplayInputFramePhase input)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
        _debug = debug ?? throw new ArgumentNullException(nameof(debug));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _routeRequestsSeen = debug.NavRouteRequests;
        _walkRequestsSeen = debug.NavWalkRequests;
    }

    public void Tick(UpdateFrameTiming timing)
    {
        _walk.ShowGrid = _debug.NavMeshVisible;
        if (_debug.NavRouteRequests != _routeRequestsSeen)
        {
            _routeRequestsSeen = _debug.NavRouteRequests;
            if (Selected() is { } target)
                _walk.RouteTo(target);
        }
        if (_debug.NavWalkRequests != _walkRequestsSeen)
        {
            _walkRequestsSeen = _debug.NavWalkRequests;
            if (_walk.IsBusy)
                _walk.Stop();
            else if (Selected() is { } target)
                _walk.WalkTo(target);
        }
        _walk.Tick(timing.SimulationDeltaSeconds);
        _input.Tick(timing);
    }

    private uint? Selected()
    {
        if (_selection.SelectedObjectId is { } selected)
            return selected;
        _debug.ReportNavigation("Navigation: select an object first");
        return null;
    }
}
