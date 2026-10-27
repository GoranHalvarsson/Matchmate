"""Small synthetic parser checks, not fabricated source evidence."""
import unittest
from inspect_passage import tracking_audit, event_audit


def frame(n, clock, players=(1,)):
    return {'frame': n, 'timestamp': clock, 'period': 1,
            'player_data': [{'player_id': p,'x': 0.,'y': 0.,'is_detected': True} for p in players],
            'ball_data': {'x': 0.,'y': 0.,'is_detected': True}}


ROSTER = {1:{'team_id':1,'first_name':'Test','last_name':'Only','number':1}}


def event(event_id='8_1', start=610, end=612, actor='1'):
    return {'event_id':event_id,'event_type':'player_possession','event_type_id':'8',
            'event_subtype':'','event_subtype_id':'','period':'1',
            'frame_start':str(start),'frame_end':str(end),'time_start':'01:00.0','time_end':'01:00.2',
            'player_id':actor,'player_in_possession_id':'','player_targeted_id':''}


class PassageTests(unittest.TestCase):
    def test_missing_frame_is_not_empty_position_frame(self):
        rows=[frame(610,'00:01:00.0'),frame(612,'00:01:00.2',())]
        report,_=tracking_audit(rows,ROSTER,60,60.2)
        self.assertEqual(report['missing_expected_frame_ids'],[611])
        self.assertEqual(report['zero_player_frame_ranges_inclusive'],[[612,612]])
        self.assertEqual(report['finite_player_xy_count_distribution'],{1:1,0:1})

    def test_timestamp_offset_measured_and_nonzero_event_residual_retained(self):
        rows=[frame(610,'00:01:00.0'),frame(612,'00:01:00.2')]
        report,frames=tracking_audit(rows,ROSTER,60,60.2)
        self.assertEqual(report['frame_div_10_minus_clock_seconds_distribution'],{'1.0':2})
        e=event();e['time_end']='01:00.1'
        events=event_audit([e],frames,ROSTER)
        self.assertEqual(events['endpoint_clock_residual_seconds_distribution'],{'0.0':1,'-0.1':1})

    def test_boundary_endpoint_is_not_claimed_joined_and_same_frame_events_survive(self):
        _,frames=tracking_audit([frame(610,'00:01:00.0'),frame(612,'00:01:00.2')],ROSTER,60,60.2)
        events=event_audit([event('8_1',609,612),event('8_2',609,612)],frames,ROSTER)
        self.assertEqual(events['overlapping_rows'],2)
        self.assertEqual(events['partial_boundary_event_ids'],['8_1','8_2'])
        self.assertEqual(events['endpoints_outside_selected_passage'],2)
        self.assertEqual(events['identity_endpoint_checks_where_frame_present'],2)
        self.assertEqual(events['same_start_frame_groups'],1)

    def test_unknown_actor_stays_explicit(self):
        _,frames=tracking_audit([frame(610,'00:01:00.0'),frame(612,'00:01:00.2')],ROSTER,60,60.2)
        events=event_audit([event(actor='999')],frames,ROSTER)
        self.assertEqual(events['unknown_endpoint_roster_ids'],[999])
        self.assertEqual(events['identity_not_in_present_endpoint_frame'],2)

    def test_physical_start_candidate_does_not_overwrite_original_start(self):
        _,frames=tracking_audit([frame(610,'00:01:00.0'),frame(612,'00:01:00.2')],ROSTER,60,60.2)
        e=event(start=612);e['frame_physical_start']='610'
        events=event_audit([e],frames,ROSTER)
        self.assertEqual(events['endpoint_residuals_by_edge_and_category']['start']['player_possession'],{'-0.2':1})
        self.assertEqual(events['physical_start_comparison']['clock_residual_seconds_distribution'],{'0.0':1})
        self.assertEqual(events['physical_start_comparison']['original_nonzero_start_residuals_zero_at_physical_start'],1)


if __name__=='__main__':
    unittest.main()
