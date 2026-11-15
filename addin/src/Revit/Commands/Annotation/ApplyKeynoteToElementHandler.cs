// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/ApplyKeynoteToElementHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: each change says whether the keynote was set on the element or on its type (a type keynote
// changes every instance of that type) and which element holds it; when no element could be changed the
// transaction is rolled back and the command fails; checks run once for dry runs and real runs alike.
// ReadOnly is false: dry_run only reports, but the same command writes otherwise.

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

    public sealed class ApplyKeynoteToElementHandler : IRevitCommand
    {
        public string Name => "apply_keynote_to_element";
        public string Description =>
            "Set the Keynote value on elements (on the element itself, else on its type, which affects every instance of that type). "
            + "keynote \"\" clears it. dry_run reports old and new values without changing anything.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""keynote"":{""type"":""string""},
            ""dry_run"":{""type"":""boolean""}},
            ""required"":[""element_ids"",""keynote""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var ids = ((JArray)p["element_ids"]).Select(t => t.Value<long>()).Distinct().ToList();
            if (ids.Count == 0) return CommandResult.Fail("INVALID_PARAMS: element_ids must not be empty.");
            var keynote = p.Value<string>("keynote");
            var dryRun = p.Value<bool?>("dry_run") ?? false;

            var changes = new JArray();
            var failed = new JArray();
            Transaction tx = null;
            try
            {
                if (!dryRun)
                {
                    tx = new Transaction(doc, "B2M: Apply Keynote");
                    tx.Start();
                }
                foreach (var id in ids)
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    string error = null;
                    Parameter parameter = null;
                    Element holder = null;
                    if (element == null) error = "element not found.";
                    else if ((parameter = FindKeynote(doc, element, out holder)) == null) error = "the element and its type have no Keynote parameter.";
                    else if (parameter.IsReadOnly) error = "the Keynote parameter is read-only.";
                    else if (parameter.StorageType != StorageType.String) error = "the Keynote parameter is not text.";
                    if (error == null)
                    {
                        var old = parameter.AsString() ?? "";
                        try
                        {
                            if (!dryRun && !parameter.Set(keynote)) throw new InvalidOperationException("Revit refused the value.");
                            changes.Add(new JObject
                            {
                                ["element_id"] = id,
                                ["applied_to"] = holder.Id == element.Id ? "instance" : "type",
                                ["holder_id"] = RevitCompat.GetId(holder.Id),
                                ["old_keynote"] = old,
                                ["new_keynote"] = keynote,
                                ["parameter_name"] = parameter.Definition.Name
                            });
                            continue;
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }
                    }
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = error });
                }

                var result = new JObject
                {
                    ["dry_run"] = dryRun,
                    ["requested"] = ids.Count,
                    ["updated"] = changes.Count,
                    ["changes"] = changes,
                    ["failed"] = failed
                };
                if (changes.Count == 0)
                {
                    if (tx != null && tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("KEYNOTE_FAILED: no element " + (dryRun ? "can" : "could") + " take the keynote; see failed. Nothing was changed.", result);
                }
                if (tx != null && tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                return CommandResult.Ok(result);
            }
            catch (Exception ex)
            {
                if (tx != null && tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                return CommandResult.Fail("KEYNOTE_FAILED: could not apply the keynote: " + ex.Message + " Nothing was changed.");
            }
            finally
            {
                tx?.Dispose();
            }
        }

        /// <summary>The element's Keynote parameter, else its type's; holder is the element that carries it.</summary>
        private static Parameter FindKeynote(Document doc, Element element, out Element holder)
        {
            holder = element;
            var parameter = element.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? element.LookupParameter("Keynote");
            if (parameter != null) return parameter;
            var type = doc.GetElement(element.GetTypeId());
            if (type == null) return null;
            holder = type;
            return type.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? type.LookupParameter("Keynote");
        }
    }
}
