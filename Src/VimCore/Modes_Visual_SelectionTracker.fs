﻿#light

namespace Vim.Modes.Visual
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open Vim

/// Responsible for tracking and updating the selection while we are in visual mode
type internal SelectionTracker
    (
        _textView : ITextView,
        _localSettings : IVimLocalSettings,
        _incrementalSearch : IIncrementalSearch,
        _visualKind : VisualKind
    ) as this =

    let _globalSettings = _localSettings.GlobalSettings

    /// The anchor point we are currently tracking.  This is always included in the selection which
    /// is created by this type 
    let mutable _anchorPoint : SnapshotPoint option = None

    /// Should the selection be extended into the line break 
    let mutable _extendIntoLineBreak : bool = false

    /// When we are in the middle of an incremental search this will 
    /// track the most recent search result
    let mutable _lastIncrementalSearchResult : SearchResult option = None

    let mutable _textChangedHandler = ToggleHandler.Empty
    do 
        _textChangedHandler <- ToggleHandler.Create (_textView.TextBuffer.Changed) (fun (args:TextContentChangedEventArgs) -> this.OnTextChanged(args))

        _incrementalSearch.CurrentSearchUpdated
        |> Observable.add (fun args -> _lastIncrementalSearchResult <- Some args.SearchResult)
        
        _incrementalSearch.CurrentSearchCancelled
        |> Observable.add (fun _ -> _lastIncrementalSearchResult <- None)

        _incrementalSearch.CurrentSearchCompleted 
        |> Observable.add (fun _ -> _lastIncrementalSearchResult <- None)

    member x.AnchorPoint = Option.get _anchorPoint

    member x.IsRunning = Option.isSome _anchorPoint

    /// Call when selection tracking should begin.  
    member x.Start() = 
        if x.IsRunning then invalidOp Vim.Resources.SelectionTracker_AlreadyRunning
        _textChangedHandler.Add()

        let selection = _textView.Selection
        if selection.IsEmpty then

            // Set the selection.  If this is line mode we need to select the entire line 
            // here
            let caretPoint = TextViewUtil.GetCaretPoint _textView
            let visualSelection = VisualSelection.CreateInitial _visualKind caretPoint _localSettings.TabStop
            visualSelection.VisualSpan.Select _textView Path.Forward

            _anchorPoint <- Some caretPoint
            _extendIntoLineBreak <- false
        else 
            // The selection is already set and we need to track it.  The anchor point in
            // vim is always included in the selection but in the ITextSelection it is 
            // not when the selection is reversed.  We need to account for this when 
            // setting our anchor point 
            _textView.Selection.Mode <- _visualKind.TextSelectionMode
            let anchorPoint = selection.AnchorPoint.Position
            _anchorPoint <- 
                if selection.IsReversed then
                    SnapshotPointUtil.SubtractOneOrCurrent anchorPoint |> Some
                else
                    Some anchorPoint
            _extendIntoLineBreak <- _visualKind = VisualKind.Character && selection.AnchorPoint.IsInVirtualSpace

    /// Called when selection should no longer be tracked.  Must be paired with Start calls or
    /// we will stay attached to certain event handlers
    member x.Stop() =
        if not x.IsRunning then invalidOp Resources.SelectionTracker_NotRunning
        _textChangedHandler.Remove()
        _anchorPoint <- None

    /// Update the selection based on the current state of the ITextView
    member x.UpdateSelection() = 

        match _anchorPoint with
        | None -> ()
        | Some anchorPoint ->
            let simulatedCaretPoint = 
                let caretPoint = TextViewUtil.GetCaretPoint _textView 
                if _incrementalSearch.InSearch then
                    match _lastIncrementalSearchResult with
                    | None -> caretPoint
                    | Some searchResult ->
                        match searchResult with
                        | SearchResult.NotFound _ -> caretPoint
                        | SearchResult.Found (_, span, _, _) -> span.Start
                else
                    caretPoint

            // Update the selection only.  Don't move the caret here.  It's either properly positioned
            // or we're simulating the selection based on incremental search
            let visualSelection = VisualSelection.CreateForPoints _visualKind anchorPoint simulatedCaretPoint _localSettings.TabStop
            let visualSelection = visualSelection.AdjustForExtendIntoLineBreak _extendIntoLineBreak
            let visualSelection = visualSelection.AdjustForSelectionKind _globalSettings.SelectionKind
            visualSelection.Select _textView

    /// Update the selection based on the current state of the ITextView
    member x.UpdateSelectionWithAnchorPoint anchorPoint = 
        if not x.IsRunning then invalidOp Resources.SelectionTracker_NotRunning

        _anchorPoint <- Some anchorPoint
        x.UpdateSelection()

    /// When the text is changed it invalidates the anchor point.  It needs to be forwarded to
    /// the next version of the buffer.  If it's not present then just go to point 0
    member x.OnTextChanged (args : TextContentChangedEventArgs) =
        match _anchorPoint with
        | None -> ()
        | Some anchorPoint ->

            _anchorPoint <- 
                match TrackingPointUtil.GetPointInSnapshot anchorPoint PointTrackingMode.Negative args.After with
                | None -> SnapshotPoint(args.After, 0) |> Some
                | Some anchorPoint -> Some anchorPoint

    interface ISelectionTracker with 
        member x.VisualKind = _visualKind
        member x.IsRunning = x.IsRunning
        member x.Start () = x.Start()
        member x.Stop () = x.Stop()
        member x.UpdateSelection() = x.UpdateSelection()
        member x.UpdateSelectionWithAnchorPoint anchorPoint = x.UpdateSelectionWithAnchorPoint anchorPoint
