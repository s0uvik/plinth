// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/CreateTextNoteHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: in plan views x/y are model X/Y in mm (other views: mm along the view's right/up from its
// origin, as before); with no text_type_id the project's default text type is used and a project without one is
// an error listing the text types (rvt-mcp fell back to the first text type found); a bad text_type_id lists the
// valid ones; mm conversion goes through Mm.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Annotation
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateTextNoteHandler : IRevitCommand
    {
        public string Name => "create_text_note";
        public string Description =>
            "Create a text note in a view (view_id, default the active view). Plan views: x/y are model coordinates in mm; "
            + "other views: mm along the view's right/up from its origin. Optional text_type_id (default: project default), "
            + "width in mm to wrap (0 = no wrap), rotation_deg.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""text"":{""type"":""string""},
            ""x"":{""type"":""number""},
            ""y"":{""type"":""number""},
            ""view_id"":{""type"":""integer""},
            ""text_type_id"":{""type"":""integer""},
            ""width"":{""type"":""number""},
            ""rotation_deg"":{""type"":""number""}},
            ""required"":[""text"",""x"",""y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var text = p.Value<string>("text");
            if (string.IsNullOrEmpty(text)) return CommandResult.Fail("INVALID_PARAMS: text cannot be empty.");
            var width = p.Value<double?>("width") ?? 0;
            if (width < 0) return CommandResult.Fail("INVALID_PARAMS: width cannot be negative.");
            var rotationDeg = p.Value<double?>("rotation_deg") ?? 0;

            if (!AnnotationHelpers.TryAnnotationView(doc, p.Value<long?>("view_id"), out View view, out var failure)) return failure;

            var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(t => t.Name).ToList();
            var typeId = p.Value<long?>("text_type_id");
            var textType = typeId.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as TextNoteType
                : doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType;
            if (textType == null)
                return CommandResult.Fail("TYPE_NOT_FOUND: " + (typeId.HasValue
                        ? "no text note type has id " + typeId.Value + "."
                        : "the project has no default text note type; pass text_type_id."),
                    new JObject { ["valid_text_types"] = new JArray(types.Take(100).Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name })) });

            var position = AnnotationHelpers.ViewPoint(view, p.Value<double>("x"), p.Value<double>("y"));
            TextNote note;
            using (var tx = new Transaction(doc, "B2M: Create Text Note"))
            {
                tx.Start();
                try
                {
                    note = width > 0
                        ? TextNote.Create(doc, view.Id, position, Mm.ToFeet(width), text, textType.Id)
                        : TextNote.Create(doc, view.Id, position, text, textType.Id);
                    if (note == null) throw new InvalidOperationException("Revit returned no text note.");
                    if (Math.Abs(rotationDeg) > 0.001)
                    {
                        var axis = Line.CreateBound(position, position + view.ViewDirection);
                        ElementTransformUtils.RotateElement(doc, note.Id, axis, rotationDeg * Math.PI / 180.0);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the text note: " + ex.Message + " Nothing was created.");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["text_note_id"] = RevitCompat.GetId(note.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["text_type_id"] = RevitCompat.GetId(note.GetTypeId()),
                ["text_type_name"] = textType.Name,
                ["position_mm"] = AnnotationHelpers.PointMm(note.Coord),
                ["width_mm"] = Mm.FromFeet(note.Width),
                ["rotation_deg"] = rotationDeg
            });
        }
    }
}
