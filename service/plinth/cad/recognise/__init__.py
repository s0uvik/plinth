"""Plan recognition (PRD Step 9 T3): a floor or roof plan sheet -> ``plinth.cad.building.PlanRecognition``.

    from plinth.cad.recognise import recognise_plan
    rec = recognise_plan(sheet, classification)

General by construction: elements are found by their drawn shape (line pairs, swings, solid fills, tread runs,
crosses, labels), never by a layer, block name, size or position known in advance. Every element keeps the handles
it came from; what is unclear becomes a Finding; what nothing used is listed as unclaimed.
"""

from plinth.cad.recognise.context import Params
from plinth.cad.recognise.plan import Report, recognise_plan, recognise_plan_detailed

__all__ = ["Params", "Report", "recognise_plan", "recognise_plan_detailed"]
