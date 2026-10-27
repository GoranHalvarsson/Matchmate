"""Authored synthetic web-text examples; no retained third-party fixture rows."""
import unittest
from audit_venue_catalogue import audit_windows

class VenueAuditTests(unittest.TestCase):
    def setUp(self):
        self.expected=[{"date":"2016-08-13","team1":"Alpha FC","team2":"Beta FC","score":{"ft":[1,0]}},{"date":"2016-08-14","team1":"Beta FC","team2":"Alpha FC","score":{"ft":[0,0]}}]
        self.aliases={"Alpha":"Alpha FC","Beta":"Beta FC"};self.venues={"North Ground":"north","South Ground":"south"}
        self.one="L10: cite1†13/08/2016 | Alpha | 1 0 | Beta | North Ground | Ref A"
        self.two="L11: cite2†14/08/2016 | Beta | 0 0 | Alpha | South Ground | Ref B"
    def run_audit(self,*texts):return audit_windows([{"pattern":"synthetic","text":t} for t in texts],self.expected,self.aliases,self.venues)
    def test_overlapping_windows_count_fixture_once(self):
        result=self.run_audit(self.one+" "+self.two,self.one)
        self.assertEqual((result["rows"],result["exactJoined"],result["overlapping_repeats"]),(2,2,1))
    def test_missing_fixture_rejected(self):
        with self.assertRaisesRegex(ValueError,"fixture key mismatch: missing=1 extra=0"):self.run_audit(self.one)
    def test_duplicate_fixture_on_distinct_line_rejected(self):
        with self.assertRaisesRegex(ValueError,"duplicate source fixture key"):self.run_audit(self.one+" "+self.two,self.one.replace("L10:","L12:"))
    def test_conflicting_overlap_rejected(self):
        with self.assertRaisesRegex(ValueError,"conflicting overlapping source row"):self.run_audit(self.one+" "+self.two,self.one.replace("1 0","2 0"))
    def test_wrong_played_date_rejected(self):
        with self.assertRaisesRegex(ValueError,"fixture key mismatch: missing=1 extra=1"):self.run_audit(self.one,self.two.replace("14/08/2016","15/08/2016"))
    def test_blank_venue_rejected(self):
        with self.assertRaisesRegex(ValueError,"blank source venue"):self.run_audit(self.one,self.two.replace("South Ground",""))
    def test_unknown_venue_never_uses_usual_home_ground(self):
        with self.assertRaisesRegex(ValueError,"unmapped source venue; home ground is not a fallback"):self.run_audit(self.one,self.two.replace("South Ground","Neutral Ground"))

if __name__ == "__main__":unittest.main()
