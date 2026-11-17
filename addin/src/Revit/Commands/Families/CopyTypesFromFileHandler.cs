// New in Plinth (Step 10, F1): copies chosen family types (their family comes with them) and MEP types from a template or
// project file into the active project as NEW types. Duplicate before edit: a type the project already has (same kind
// and name; for a family type, same family and category) is reported "already_there" with its id and nothing is
// copied for it; anything else Revit brings along (fitting families of a pipe type's routing, materials) uses the
// project's own types of the same name (IDuplicateTypeNamesHandler: UseDestinationTypes), so no existing type is
// overwritten or edited. One transaction; each item is copied in its own sub-transaction so one failure is reported
// and leaves nothing behind. created_ids lists every element the call added (found by comparing the project's
// element ids before and after), so a run can later delete exactly what it made. The source file is opened in the
// background and closed unsaved (SourceDocument); refused inside batch_execute.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class CopyTypesFromFileHandler : IRevitCommand
    {
        private const int MaxItems = 100;
        private static readonly string[] ItemFields = { "source_id", "kind", "name", "family", "category" };

        public string Name => "copy_types_from_file";
        public string Description =>
            "Copy family types (with their family) and MEP types from a template or project file (absolute .rte/.rvt path) into the active "
            + "project as new types. items: [{source_id}] (id from list_types_in_file) or [{kind, name, family?, category?}] "
            + "(kind: " + FileTypeKinds.AllText + "). Per item: copied (new_id), already_there (existing_id; nothing changed: an "
            + "existing type is never overwritten or edited), not_found, ambiguous or failed. Dependent types Revit brings along use "
            + "the project's own types of the same name. created_ids lists every element added. dry_run reports without copying. "
            + "Not allowed inside batch_execute.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""path"":{""type"":""string"",""description"":""Absolute path to a .rte or .rvt file""},
            ""items"":{""type"":""array"",""items"":{""type"":""object""}},
            ""dry_run"":{""type"":""boolean""}},
            ""required"":[""path"",""items""]}";
        public bool ReadOnly => false;

        private sealed class Item
        {
            public int Index;
            public long? SourceId;
            public string Kind;
            public string TypeName;
            public string Family;
            public string Category;

            public Element Source;
            public JObject Result;
        }

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public int Calls;

            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                Calls++;
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: the active document is a family; types are copied into a project.");
            if (!TryItems((JArray)p["items"], out var items, out var failure)) return failure;
            if (!SourceDocument.TryResolvePath(p.Value<string>("path"), out var fullPath, out _, out failure)) return failure;
            var dryRun = p.Value<bool?>("dry_run") ?? false;

            if (!SourceDocument.TryOpen(app, fullPath, out var source, out failure)) return failure;
            using (source)
            {
                if (source.IsActive || source.Doc.Equals(doc))
                    return CommandResult.Fail("SAME_DOCUMENT: the file is the active project itself; nothing to copy.");

                foreach (var item in items) Resolve(source.Doc, doc, item);
                var pending = items.Where(i => i.Result == null).ToList();
                if (dryRun || pending.Count == 0)
                {
                    foreach (var item in pending) item.Result = Status(item, "would_copy");
                    return CommandResult.Ok(Summary(source, items, new JArray(), dryRun, 0));
                }

                var before = AllIds(doc);
                var handler = new UseDestinationTypes();
                using (var tx = new Transaction(doc, "Plinth: Copy types from " + System.IO.Path.GetFileName(fullPath)))
                {
                    tx.Start();
                    try
                    {
                        var options = new CopyPasteOptions();
                        options.SetDuplicateTypeNamesHandler(handler);
                        foreach (var item in pending) Copy(source.Doc, doc, item, options, before);

                        if (tx.Commit() != TransactionStatus.Committed)
                            return CommandResult.Fail("NOT_COMMITTED: Revit rolled the copy back; see failures. Nothing was copied.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return CommandResult.Fail("COPY_FAILED: " + ex.Message + " Nothing was copied.");
                    }
                }

                var created = AllIds(doc);
                created.ExceptWith(before);
                return CommandResult.Ok(Summary(source, items, new JArray(created.OrderBy(id => id)), false, handler.Calls));
            }
        }

        private static bool TryItems(JArray raw, out List<Item> items, out CommandResult failure)
        {
            items = new List<Item>();
            failure = null;
            if (raw.Count == 0 || raw.Count > MaxItems)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: items needs 1 to " + MaxItems + " entries.");
                return false;
            }
            for (var i = 0; i < raw.Count; i++)
            {
                var entry = raw[i] as JObject;
                string Bad(string why) => "INVALID_PARAMS: items[" + i + "] " + why;
                if (entry == null) { failure = CommandResult.Fail(Bad("must be an object.")); return false; }
                var unknown = entry.Properties().Select(x => x.Name).FirstOrDefault(n => !ItemFields.Contains(n));
                if (unknown != null) { failure = CommandResult.Fail(Bad("has unknown field '" + unknown + "'. Valid: " + string.Join(", ", ItemFields) + ".")); return false; }
                var item = new Item
                {
                    Index = i,
                    SourceId = entry["source_id"]?.Type == JTokenType.Integer ? entry.Value<long>("source_id") : (long?)null,
                    Kind = entry.Value<string>("kind")?.Trim(),
                    TypeName = entry.Value<string>("name")?.Trim(),
                    Family = entry.Value<string>("family")?.Trim(),
                    Category = entry.Value<string>("category")?.Trim()
                };
                if (entry["source_id"] != null && !item.SourceId.HasValue) { failure = CommandResult.Fail(Bad("source_id must be an integer.")); return false; }
                if (!item.SourceId.HasValue && (string.IsNullOrEmpty(item.Kind) || string.IsNullOrEmpty(item.TypeName)))
                {
                    failure = CommandResult.Fail(Bad("needs source_id, or kind and name."));
                    return false;
                }
                if (!string.IsNullOrEmpty(item.Kind) && !FileTypeKinds.IsKnown(item.Kind))
                {
                    failure = CommandResult.Fail(Bad("kind '" + item.Kind + "' is unknown. Valid kinds: " + FileTypeKinds.AllText + "."));
                    return false;
                }
                if (!string.IsNullOrEmpty(item.Category) && !Lookup.TryBuiltInCategory(item.Category, out _, out failure)) return false;
                items.Add(item);
            }
            return true;
        }

        /// <summary>Finds the item in the source and checks whether the project already has it; sets Result unless it is to be copied.</summary>
        private static void Resolve(Document source, Document doc, Item item)
        {
            if (item.SourceId.HasValue)
            {
                var element = source.GetElement(RevitCompat.ToElementId(item.SourceId.Value));
                var kind = FileTypeKinds.KindOf(element);
                if (kind == null || (!string.IsNullOrEmpty(item.Kind) && item.Kind != kind))
                {
                    item.Result = Status(item, "not_found", "the file has no " + (item.Kind ?? "listed type") + " with id " + item.SourceId.Value
                        + " (ids come from list_types_in_file for this same file).");
                    return;
                }
                item.Source = element;
            }
            else
            {
                var matches = FileTypeKinds.TypesOf(source, item.Kind).Where(t => Matches(t, item)).ToList();
                if (matches.Count != 1)
                {
                    item.Result = Status(item, matches.Count == 0 ? "not_found" : "ambiguous",
                        matches.Count == 0 ? "the file has no " + item.Kind + " named '" + item.TypeName + "'" + (item.Family == null ? "" : " in family '" + item.Family + "'") + "."
                            : matches.Count + " types match; pass family/category or source_id.");
                    item.Result["candidates"] = new JArray(matches.Take(20).Select(m => (JToken)FileTypeKinds.Describe(source, m)));
                    return;
                }
                item.Source = matches[0];
            }
            item.Kind = FileTypeKinds.KindOf(item.Source);
            var existing = FileTypeKinds.TypesOf(doc, item.Kind).FirstOrDefault(t => FileTypeKinds.SameType(t, item.Source));
            if (existing == null) return;
            item.Result = Status(item, "already_there", "the project already has this type; it was left unchanged.");
            item.Result["existing_id"] = RevitCompat.GetId(existing.Id);
        }

        private static bool Matches(Element type, Item item)
        {
            if (!string.Equals(type.Name, item.TypeName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!(type is FamilySymbol symbol)) return true;
            return (string.IsNullOrEmpty(item.Family) || string.Equals(symbol.FamilyName, item.Family, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(item.Category) || Lookup.BuiltInName(symbol.Category) == item.Category);
        }

        private static void Copy(Document source, Document doc, Item item, CopyPasteOptions options, HashSet<long> before)
        {
            var familyExisted = item.Source is FamilySymbol fs && new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Any(f => string.Equals(f.Name, fs.FamilyName, StringComparison.OrdinalIgnoreCase));
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    var copied = ElementTransformUtils.CopyElements(source, new List<ElementId> { item.Source.Id }, doc, Transform.Identity, options);
                    var newId = copied?.FirstOrDefault();
                    var element = newId == null ? null : doc.GetElement(newId);
                    if (element == null) throw new InvalidOperationException("Revit copied nothing.");
                    if (before.Contains(RevitCompat.GetId(newId)))
                    {
                        // Revit matched it to a project type after all: nothing new was made for this item.
                        sub.RollBack();
                        item.Result = Status(item, "already_there", "Revit matched it to an existing project type; it was left unchanged.");
                        item.Result["existing_id"] = RevitCompat.GetId(newId);
                        return;
                    }
                    sub.Commit();
                    item.Result = Status(item, "copied");
                    item.Result["new_id"] = RevitCompat.GetId(newId);
                    item.Result["new_name"] = element.Name;
                    if (element is FamilySymbol symbol)
                    {
                        item.Result["family_id"] = RevitCompat.GetId(symbol.Family.Id);
                        item.Result["new_family_name"] = symbol.FamilyName;
                        item.Result["into_existing_family"] = familyExisted;
                    }
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    item.Result = Status(item, "failed", "Revit could not copy it: " + ex.Message);
                }
            }
        }

        private static JObject Status(Item item, string status, string reason = null)
        {
            var o = new JObject { ["index"] = item.Index, ["status"] = status };
            if (item.Source != null)
            {
                o["source_id"] = RevitCompat.GetId(item.Source.Id);
                o["kind"] = FileTypeKinds.KindOf(item.Source);
                o["name"] = item.Source.Name;
                if (item.Source is FamilySymbol symbol)
                {
                    o["family"] = symbol.FamilyName;
                    o["category"] = Lookup.BuiltInName(symbol.Category);
                }
            }
            else
            {
                o["source_id"] = item.SourceId;
                o["kind"] = item.Kind;
                o["name"] = item.TypeName;
                if (item.Family != null) o["family"] = item.Family;
            }
            if (reason != null) o["reason"] = reason;
            return o;
        }

        private static JObject Summary(SourceDocument source, List<Item> items, JArray created, bool dryRun, int dependentsReused)
        {
            var results = new JArray(items.Select(i => (JToken)i.Result));
            int Count(string s) => items.Count(i => i.Result.Value<string>("status") == s);
            var summary = source.Describe();
            summary["dry_run"] = dryRun;
            summary["copied"] = Count("copied");
            summary["already_there"] = Count("already_there");
            summary["not_found"] = Count("not_found") + Count("ambiguous");
            summary["failed"] = Count("failed");
            summary["items"] = results;
            summary["created_ids"] = created;
            summary["duplicate_names_resolved_to_project_types"] = dependentsReused;
            return summary;
        }

        private static HashSet<long> AllIds(Document doc)
        {
            var ids = new HashSet<long>();
            foreach (var id in new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds()) ids.Add(RevitCompat.GetId(id));
            foreach (var id in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()) ids.Add(RevitCompat.GetId(id));
            return ids;
        }
    }
}
