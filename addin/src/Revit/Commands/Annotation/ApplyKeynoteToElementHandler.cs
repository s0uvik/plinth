// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/ApplyKeynoteToElementHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: each change says whether the keynote was set on the element or on its type (a type keynote
// changes every instance of that type) and which element holds it; when no element could be changed the
// transaction is rolled back and the command fails; checks run once for dry runs and real runs alike.
// ReadOnly is false: dry_run only reports, but the same command writes otherwise.
// Duplicate before edit (TypeEditGuard): when the keynote lives on the type (or an id is itself a type), the type is
// changed only with duplicate_as (the copy gets the keynote and only the given elements of that type move onto it) or
// confirm_edit_all (= its exact instance count); otherwise TYPE_IN_USE, also for dry runs, and nothing changes.

using System;
using System.Collections.Generic;
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
            + "keynote \"\" clears it. dry_run reports old and new values without changing anything." + TypeEditGuard.Rule
            + " Several types in one call: duplicate_as {\"<typeId>\": \"name\"} and confirm_edit_all {\"<typeId>\": N}.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""keynote"":{""type"":""string""},
            ""dry_run"":{""type"":""boolean""},
            " + TypeEditGuard.SchemaFields + @"},
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

            // Resolve every element first, so the duplicate-before-edit check sees every type this call would change.
            var work = new List<(long id, Element element, Parameter parameter, Element holder, string error)>();
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
                work.Add((id, element, parameter, holder, error));
            }
            var edits = work.Where(w => w.error == null && TypeEditGuard.IsShared(w.holder))
                .GroupBy(w => RevitCompat.GetId(w.holder.Id))
                .Select(g => new SharedEdit(g.First().holder, g.Where(w => w.element.Id != w.holder.Id).Select(w => w.id)));
            if (!TypeEditGuard.TryPlan(doc, p, edits, out var plan, out var failure)) return failure;

            var changes = new JArray();
            var failed = new JArray();
            Transaction tx = null;
            try
            {
                if (!dryRun)
                {
                    tx = new Transaction(doc, "B2M: Apply Keynote");
                    tx.Start();
                    plan.Apply(doc);
                }
                var done = new HashSet<long>();
                foreach (var w in work)
                {
                    var error = w.error;
                    if (error == null)
                    {
                        var old = w.parameter.AsString() ?? "";
                        try
                        {
                            var target = dryRun ? w.holder : plan.TargetFor(w.holder);
                            // A type is set once, however many of the given elements use it.
                            if (!dryRun && done.Add(RevitCompat.GetId(target.Id)))
                            {
                                var parameter = target.Id == w.holder.Id ? w.parameter : FindOwn(target);
                                if (parameter == null || parameter.IsReadOnly || !parameter.Set(keynote))
                                {
                                    if (target.Id != w.holder.Id)
                                    {
                                        tx.RollBack();
                                        return CommandResult.Fail("KEYNOTE_FAILED: the copy '" + target.Name + "' did not take the keynote. Nothing was changed.");
                                    }
                                    throw new InvalidOperationException("Revit refused the value.");
                                }
                            }
                            changes.Add(new JObject
                            {
                                ["element_id"] = w.id,
                                ["applied_to"] = w.holder.Id == w.element.Id ? "instance" : "type",
                                ["holder_id"] = RevitCompat.GetId(target.Id),
                                ["old_keynote"] = old,
                                ["new_keynote"] = keynote,
                                ["parameter_name"] = w.parameter.Definition.Name
                            });
                            continue;
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }
                    }
                    failed.Add(new JObject { ["element_id"] = w.id, ["error"] = error });
                }

                var result = new JObject
                {
                    ["dry_run"] = dryRun,
                    ["requested"] = ids.Count,
                    ["updated"] = changes.Count,
                    ["changes"] = changes,
                    ["failed"] = failed,
                    ["shared_edits"] = plan.Report()
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

        private static Parameter FindOwn(Element element) =>
            element.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? element.LookupParameter("Keynote");

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
