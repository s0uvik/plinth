"""Fit-out of a built model (PRD Step 10, F14): furniture and MEP laid out from rules where the office has no drawings.

    from b2m.fitout import rules, rooms, furniture
    ruleset = rules.for_location(location)          # location first: its profile's fit-out rules (or a question)
    model = rooms.build(plan, ruleset, answers)     # every room with class, polygon, wall faces, doors, windows, flat
    layout = furniture.solve(model, ruleset)        # standard furniture per room; what does not fit is listed

Everything works from a ``BuildPlan`` alone, so a model made from a brief and one made from DWGs fit out the same
way. Rule values are unverified placeholders until checked (each carries its source); the result is an indicative
coordination model, never an engineer's design.
"""
