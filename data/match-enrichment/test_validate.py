import copy
import unittest
from validate import load, validate

class ResearchPackTests(unittest.TestCase):
    def setUp(self): self.pack=copy.deepcopy(load())
    def test_valid_and_baseline(self):
        result=validate(self.pack)
        self.assertEqual(result['substitutions'],{'chelsea':4,'brentford':5})
        self.assertEqual(result['goals'],{'brentford':3,'chelsea':0})
    def test_unknown_not_zero(self):
        self.pack['coverage']['red_cards']=0
        with self.assertRaisesRegex(ValueError,'unknown coverage'): validate(self.pack)
    def test_wrong_source(self):
        self.pack['goals'][0]['evidence']['source']='missing'
        with self.assertRaisesRegex(ValueError,'unknown source'): validate(self.pack)
    def test_missing_fact_citation(self):
        del self.pack['fixture']['crowd']['evidence']
        with self.assertRaisesRegex(ValueError,'fixture fact missing evidence'): validate(self.pack)
    def test_empty_crowd_citation(self):
        self.pack['fixture']['crowd']['evidence']={}
        with self.assertRaisesRegex(ValueError,'unknown source or missing locator'): validate(self.pack)
    def test_crowd_citation_without_source(self):
        del self.pack['fixture']['crowd']['evidence']['source']
        with self.assertRaisesRegex(ValueError,'unknown source or missing locator'): validate(self.pack)
    def test_empty_coach_role_citation(self):
        self.pack['coach_contexts'][0]['role_evidence']={}
        with self.assertRaisesRegex(ValueError,'unknown source or missing locator'): validate(self.pack)
    def test_whitespace_citation_locator(self):
        self.pack['fixture']['result']['corroboration']['locator']='  \t'
        with self.assertRaisesRegex(ValueError,'unknown source or missing locator'): validate(self.pack)
    def test_citation_list_checks_every_item(self):
        self.pack['goals'][0]['evidence']=[self.pack['goals'][0]['evidence'],{}]
        with self.assertRaisesRegex(ValueError,'unknown source or missing locator'): validate(self.pack)
    def test_invalid_substitution(self):
        self.pack['substitutions'][0]['on']='chelsea:palmer'
        with self.assertRaisesRegex(ValueError,'invalid substitution'): validate(self.pack)
    def test_score_mismatch(self):
        self.pack['fixture']['result']['value']=[2,0]
        with self.assertRaisesRegex(ValueError,'goals do not reconcile'): validate(self.pack)
    def test_seconds_not_invented(self):
        self.pack['goals'][0]['time']['seconds']=0
        with self.assertRaisesRegex(ValueError,'unsupported time precision'): validate(self.pack)
    def test_event_before_substitution(self):
        self.pack['goals'][2]['time']={'printed':'80','minute':80,'added_minute':None,'seconds':None,'precision':'reported_minute'}
        with self.assertRaisesRegex(ValueError,'outside playing interval'): validate(self.pack)
    def test_duplicate_roster(self):
        self.pack['teams']['chelsea']['bench'][0]='chelsea:palmer'
        with self.assertRaisesRegex(ValueError,'roster identity'): validate(self.pack)
    def test_actual_kickoff_not_scheduled(self):
        self.pack['fixture']['kickoff']['actual']='20:00'
        with self.assertRaisesRegex(ValueError,'actual kickoff'): validate(self.pack)
    def test_added_time_label_consistent(self):
        self.pack['goals'][2]['time']['added_minute']=3
        with self.assertRaisesRegex(ValueError,'time label mismatch'): validate(self.pack)
    def test_missing_identity_mapping(self):
        del self.pack['players'][0]['identity_resolution']
        with self.assertRaisesRegex(ValueError,'missing public-name mapping'): validate(self.pack)
    def test_missing_identity_evidence(self):
        del self.pack['players'][0]['identity_resolution']['evidence']
        with self.assertRaisesRegex(ValueError,'missing public-name mapping or identity evidence'): validate(self.pack)
    def test_duplicate_identity_mapping(self):
        self.pack['players'][1]['identity_resolution']['public_name']=self.pack['players'][0]['identity_resolution']['public_name']
        with self.assertRaisesRegex(ValueError,'duplicate public-name mapping'): validate(self.pack)
    def test_unresolved_conflict_not_canonical(self):
        self.pack['second_report_crosscheck']['conflicts'][0]['canonical']='83'
        with self.assertRaisesRegex(ValueError,'unresolved conflict cannot'): validate(self.pack)
    def test_conflict_not_silent_agreement(self):
        row=next(r for r in self.pack['second_report_crosscheck']['categories'] if r['category']=='thiago_goal_minute')
        row['status']='agreement'
        with self.assertRaisesRegex(ValueError,'conflicting claim cannot'): validate(self.pack)
    def test_original_claim_checkpoint(self):
        self.pack['fixture']['crowd']['value']=17000
        with self.assertRaisesRegex(ValueError,'prior league assessment changed'): validate(self.pack)
    def test_missing_league_target(self):
        del self.pack['league_record_assessment']['discipline']
        with self.assertRaisesRegex(ValueError,'required league assessments missing'): validate(self.pack)
    def test_goal_selection_requires_retained_disagreement(self):
        self.pack['league_record_assessment']['goal_minute']['retained_disagreement']['value']='83'
        with self.assertRaisesRegex(ValueError,'goal league selection'): validate(self.pack)
    def test_formation_not_measured_truth(self):
        self.pack['league_record_assessment']['formation_classifications'][0]['measured_tactical_truth']=True
        with self.assertRaisesRegex(ValueError,'formation classification'): validate(self.pack)
    def test_card_minute_mismatch(self):
        self.pack['league_record_assessment']['discipline']['cards'][0]['minute']=83
        with self.assertRaisesRegex(ValueError,'league card identities/minutes'): validate(self.pack)
    def test_card_coverage_not_complete(self):
        self.pack['league_record_assessment']['discipline']['complete_card_audit']=True
        with self.assertRaisesRegex(ValueError,'listed cards are not complete'): validate(self.pack)
    def test_league_citation_not_empty(self):
        self.pack['league_record_assessment']['referee']['evidence']={}
        with self.assertRaisesRegex(ValueError,'unknown source or missing locator'): validate(self.pack)
    def test_timeline_goal_missing(self):
        self.pack['timeline_audit']['goals'].pop()
        with self.assertRaisesRegex(ValueError,'timeline goals missing'): validate(self.pack)
    def test_timeline_substitution_player_mismatch(self):
        self.pack['timeline_audit']['substitutions'][0]['on']='chelsea:palmer'
        with self.assertRaisesRegex(ValueError,'timeline substitution mismatch'): validate(self.pack)
    def test_timeline_substitution_minute_mismatch(self):
        self.pack['timeline_audit']['substitutions'][0]['time'].update(printed='72',minute=72)
        with self.assertRaisesRegex(ValueError,'timeline substitution mismatch'): validate(self.pack)
    def test_timeline_assist_not_inferred_from_narrative(self):
        self.pack['timeline_audit']['goals'][2]['formal_assist']['player']='brentford:schade'
        with self.assertRaisesRegex(ValueError,'formal assist cannot be inferred'): validate(self.pack)
    def test_timeline_announced_minimum_not_period_end(self):
        self.pack['timeline_audit']['stoppage_observations'][0]['kind']='period_end'
        with self.assertRaisesRegex(ValueError,'minimum announcements'): validate(self.pack)
    def test_timeline_no_exact_elapsed_stoppage(self):
        self.pack['timeline_audit']['stoppage_observations'][3]['exact_elapsed_seconds']=5820
        with self.assertRaisesRegex(ValueError,'no exact stoppage duration'): validate(self.pack)
    def test_timeline_absence_not_actual_zero(self):
        self.pack['timeline_audit']['decisions']['actual_red_card_total']=0
        with self.assertRaisesRegex(ValueError,'display absence cannot become actual zero'): validate(self.pack)
    def test_timeline_not_certified_complete_feed(self):
        self.pack['timeline_audit']['complete_event_feed_verified']=True
        with self.assertRaisesRegex(ValueError,'not complete feed'): validate(self.pack)
    def test_missing_match_shirt_mapping(self):
        self.pack['lineup_statistics_audit']['lineup_rows'].pop()
        with self.assertRaisesRegex(ValueError,'complete unique forty-player'): validate(self.pack)
    def test_duplicate_match_shirt_number(self):
        self.pack['lineup_statistics_audit']['lineup_rows'][1]['shirt_number']=1
        with self.assertRaisesRegex(ValueError,'shirt numbers unique'): validate(self.pack)
    def test_wrong_unique_shirt_player_relation(self):
        self.pack['lineup_statistics_audit']['lineup_rows'][0]['shirt_number']=99
        with self.assertRaisesRegex(ValueError,'shirt-player source relation'): validate(self.pack)
    def test_no_inferred_starter_tactical_role(self):
        self.pack['lineup_statistics_audit']['lineup_rows'][0]['individual_match_role']='goalkeeper'
        with self.assertRaisesRegex(ValueError,'individual tactical role'): validate(self.pack)
    def test_bench_label_not_match_role(self):
        self.pack['lineup_statistics_audit']['lineup_rows'][11]['category_semantics']='measured role'
        with self.assertRaisesRegex(ValueError,'broad category semantics'): validate(self.pack)
    def test_metric_home_away_not_reversed(self):
        self.pack['lineup_statistics_audit']['metrics'][0]['values']={'brentford':59.8,'chelsea':40.2}
        with self.assertRaisesRegex(ValueError,'metric source units or home-away'): validate(self.pack)
    def test_metric_not_half_or_snapshot(self):
        self.pack['lineup_statistics_audit']['metrics'][0]['period_breakdown']='first_half'
        with self.assertRaisesRegex(ValueError,'metric period/definition scope'): validate(self.pack)
    def test_explicit_red_zero_not_unknown(self):
        next(r for r in self.pack['lineup_statistics_audit']['metrics'] if r['id']=='red_cards')['values']['chelsea']=None
        with self.assertRaisesRegex(ValueError,'metric source units or home-away'): validate(self.pack)
    def test_anomaly_not_accepted_core_stat(self):
        self.pack['lineup_statistics_audit']['metrics'].append({'id':'shots_inside_box'})
        with self.assertRaisesRegex(ValueError,'eleven accepted core metric'): validate(self.pack)
    def test_anomaly_not_silently_repaired(self):
        self.pack['lineup_statistics_audit']['excluded_anomalies'][0]['printed_values']['brentford']=9
        with self.assertRaisesRegex(ValueError,'anomalous shot-location rows'): validate(self.pack)
if __name__=='__main__': unittest.main()
