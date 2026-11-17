"""Door/window estimate (PRD Step 9 T11): quantities + written measurement rules -> the office's Excel estimate.

    q = Quantities(project=..., openings=[...], railings=[...])
    rules = load_rules("office-default")          # plinth/estimate/rules/office-default.yaml
    est = build_estimate(q, rules)                 # rows, Python values, questions
    write_xlsx(est, rules, "estimate.xlsx")        # live formulas in the office layout
    report = compare_workbooks("estimate.xlsx", "office.xlsx", est)   # cell-by-cell difference report
"""

from plinth.estimate.compare import CompareReport, compare_workbooks
from plinth.estimate.crosscheck import DrawingObservation, crosscheck
from plinth.estimate.engine import Estimate, EstimateRow, Question, build_estimate
from plinth.estimate.quantities import Opening, ProjectInfo, Quantities, Railing, Storey
from plinth.estimate.ruleset import Rules, load_rules
from plinth.estimate.xlsx import write_xlsx

__all__ = ["CompareReport", "DrawingObservation", "Estimate", "EstimateRow", "Opening", "ProjectInfo", "Quantities",
           "Question", "Railing", "Rules", "Storey", "build_estimate", "compare_workbooks", "crosscheck",
           "load_rules", "write_xlsx"]
