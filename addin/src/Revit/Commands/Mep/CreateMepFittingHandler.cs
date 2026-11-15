// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/CreateMepFittingHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: each connectors entry is {element_id, connector_id} (rvt-mcp called the id connector_index) and
// entries are checked strictly, unknown fields included, since the schema validator does not look inside array items;
// every refusal is a failure with a CODE (rvt-mcp returned success with created=false); the response adds the
// fitting's family type.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class CreateMepFittingHandler : IRevitCommand
    {
        public string Name => "create_mep_fitting";
        public string Description =>
            "Insert a fitting joining connectors of existing MEP elements (pipes, ducts, fittings). fitting_kind: elbow, union, transition "
            + "(2 connectors), tee (3), cross (4). connectors is an array of {element_id, connector_id} (connector_id from "
            + "get_mep_element_connectors, not the ordinal). The routing preferences of the curves choose the fitting family.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""fitting_kind"":{""type"":""string"",""enum"":[""elbow"",""tee"",""union"",""cross"",""transition""]},
            ""connectors"":{""type"":""array"",""items"":{""type"":""object""}}},
            ""required"":[""fitting_kind"",""connectors""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var kind = p.Value<string>("fitting_kind");
            var required = kind == "tee" ? 3 : kind == "cross" ? 4 : 2;
            var entries = (JArray)p["connectors"];
            if (entries.Count != required)
                return CommandResult.Fail("INVALID_PARAMS: a " + kind + " needs exactly " + required + " connectors; got " + entries.Count + ".");

            var connectors = new Connector[required];
            for (var i = 0; i < required; i++)
            {
                var entry = (JObject)entries[i];
                var unknown = entry.Properties().Select(x => x.Name).FirstOrDefault(n => n != "element_id" && n != "connector_id");
                if (unknown != null)
                    return CommandResult.Fail("INVALID_PARAMS: connectors[" + i + "] has unknown field '" + unknown + "'. Valid fields: element_id, connector_id.");
                if (entry["element_id"]?.Type != JTokenType.Integer || entry["connector_id"]?.Type != JTokenType.Integer)
                    return CommandResult.Fail("INVALID_PARAMS: connectors[" + i + "] needs integer element_id and connector_id.");
                var elementId = entry.Value<long>("element_id");
                var connectorId = entry.Value<int>("connector_id");
                var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                if (element == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: no element has id " + elementId + ".");
                var manager = MepHelpers.GetConnectorManager(element);
                if (manager == null) return CommandResult.Fail("NO_CONNECTORS: element " + elementId + " has no MEP connectors.");
                connectors[i] = MepHelpers.FindConnector(manager, connectorId);
                if (connectors[i] == null)
                    return CommandResult.Fail("CONNECTOR_NOT_FOUND: element " + elementId + " has no connector with id " + connectorId + ".");
            }

            using (var tx = new Transaction(doc, "B2M: Create MEP Fitting"))
            {
                tx.Start();
                try
                {
                    FamilyInstance fitting;
                    switch (kind)
                    {
                        case "elbow": fitting = doc.Create.NewElbowFitting(connectors[0], connectors[1]); break;
                        case "transition": fitting = doc.Create.NewTransitionFitting(connectors[0], connectors[1]); break;
                        case "union": fitting = doc.Create.NewUnionFitting(connectors[0], connectors[1]); break;
                        case "tee": fitting = doc.Create.NewTeeFitting(connectors[0], connectors[1], connectors[2]); break;
                        default: fitting = doc.Create.NewCrossFitting(connectors[0], connectors[1], connectors[2], connectors[3]); break;
                    }
                    if (fitting == null) throw new InvalidOperationException("Revit returned no fitting for these connectors.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        fitting_id = RevitCompat.GetId(fitting.Id),
                        fitting_kind = kind,
                        fitting_category = fitting.Category?.Name,
                        family_name = fitting.Symbol?.FamilyName,
                        type_name = fitting.Symbol?.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the " + kind + " fitting: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
