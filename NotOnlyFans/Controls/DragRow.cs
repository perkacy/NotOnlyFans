using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace NotOnlyFans.Controls;

/// <summary>
/// A list row that can be picked up and moved. Only adds a settable mouse cursor
/// (WinUI keeps ProtectedCursor protected), so the row can show a hand while it is carried.
/// </summary>
public sealed partial class DragRow : Grid
{
    public InputCursor? Cursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}
