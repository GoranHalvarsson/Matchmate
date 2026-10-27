"""Scoped rejection checks for the review-batch validator."""
import copy
import unittest
from unittest.mock import patch
import validate

class ReviewValidationTests(unittest.TestCase):
    def reject_change(self, filename, mutate):
        original = validate.read
        changed = copy.deepcopy(original(filename))
        mutate(changed)
        with patch.object(validate, "read", side_effect=lambda name: changed if name == filename else original(name)):
            with self.assertRaises(AssertionError):
                validate.validate()

    def test_duplicate_membership_rejected(self):
        self.reject_change("memberships.json", lambda rows: rows.__setitem__(1, copy.deepcopy(rows[0])))

    def test_undated_current_observation_cannot_gain_historical_bounds(self):
        def change(rows):
            next(r for r in rows if r["club_id"] == "everton")["valid_from"] = "2015-08-01"
        self.reject_change("ground-associations.json", change)

    def test_unknown_ground_cannot_be_promoted_from_current_observation(self):
        def change(rows):
            next(r for r in rows if r["club_id"] == "west-ham")["ground_id"] = "london-stadium"
        self.reject_change("membership-ground-review.json", change)

    def test_year_precision_cannot_gain_invented_day(self):
        def change(rows):
            next(r for r in rows if r["club_id"] == "crystal-palace")["valid_from"] = "1924-01-01"
        self.reject_change("ground-associations.json", change)

    def test_coordinate_cannot_be_replaced_by_diagram_position(self):
        self.reject_change("grounds.json", lambda rows: rows[0].__setitem__("latitude", 0))

    def test_primary_context_cannot_change_club(self):
        def change(rows):
            next(r for r in rows if r["ground_evidence_status"] == "primary_reference_season_context_not_interval")["club_id"] = "arsenal"
        self.reject_change("membership-ground-review.json", change)

    def test_primary_membership_corroboration_cannot_add_club(self):
        self.reject_change("membership-corroboration.json", lambda rows: rows[0]["club_ids"].append("coventry"))

    def test_prior_season_primary_evidence_cannot_support_later_season(self):
        def change(rows):
            row = next(r for r in rows if r["membership_id"] == "2016/17|swansea")
            # Same club and ground, but the February 2016 retrospective supports 2015/16 only.
            row["primary_note_id"] = "primary-ground-13"
        original = validate.read
        changed = copy.deepcopy(original("membership-ground-review.json"))
        change(changed)
        with patch.object(validate, "read", side_effect=lambda name: changed if name == "membership-ground-review.json" else original(name)):
            with self.assertRaisesRegex(AssertionError, "^primary evidence not reviewed for this season$"):
                validate.validate()

    def reject_supplement(self, filename, mutate, message):
        original = validate.read
        changed = copy.deepcopy(original(filename))
        mutate(changed)
        with patch.object(validate, "read", side_effect=lambda name: changed if name == filename else original(name)):
            with self.assertRaisesRegex(AssertionError, message):
                validate.validate()

    def test_current_address_cannot_be_backdated_into_historical_season(self):
        self.reject_supplement("supplemental-address-reviews.json", lambda rows: rows[0].__setitem__("effective_from","2015-08-01"), "supplemental address must not gain invented validity bounds")

    def test_year_only_naming_event_cannot_gain_january_first(self):
        def change(rows):
            next(e for e in rows if e["date_precision"] == "year")["effective_from"] = "2015-01-01"
        self.reject_supplement("supplemental-naming-events.json", change, "naming date exceeds source precision")

    def test_club_locality_cannot_be_replaced_with_stadium_locality(self):
        self.reject_supplement("supplemental-club-localities.json", lambda rows: rows[0].__setitem__("football_home_town","Stadium municipality"), "football locality differs from cited club source")

    def test_announced_venue_cannot_become_actual_match_evidence(self):
        def change(data):
            next(c for c in data["cases"] if c["case_id"] == "2026-27-spurs-coventry")["event_status"] = "actual"
        self.reject_supplement("supplemental-fixture-venue-audit.json", change, "planned venue source cannot become actual confirmation")

if __name__ == "__main__":
    unittest.main()
