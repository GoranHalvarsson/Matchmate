import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from validate_bundle import DATA, read_json, validate
from season_views import season_view, player_at_fixture, revisions_at, active, stamp


class SeasonTests(unittest.TestCase):
    def test_timestamp_offsets_normalize_and_naive_time_rejected(self):
        self.assertEqual(stamp('2023-07-01T00:00:00Z'),stamp('2023-07-01T01:00:00+01:00'))
        with self.assertRaises(ValueError):
            stamp('2023-07-01T00:00:00')

    def setUp(self):
        self.bundle=read_json(DATA/'examples/seasons-bundle.json')

    def view(self, year):
        return season_view(self.bundle,f'season-{year}',f'{year}-09-10T14:00:00Z',f'{year}-09-10T14:00:00Z')

    def test_both_seasons_and_membership(self):
        self.assertEqual([],validate(self.bundle))
        self.assertEqual(['club-harbour','club-riverside'],[c['club_id'] for c in self.view(2022)['clubs']])
        self.assertEqual(['club-harbour','club-moor'],[c['club_id'] for c in self.view(2023)['clubs']])
        self.assertEqual('partial',self.view(2023)['membership_coverage'])

    def test_ground_move_and_map_coordinates(self):
        old=self.view(2022)['clubs'][0]['grounds'][0]['venue']
        new=self.view(2023)['clubs'][0]['grounds'][0]['venue']
        self.assertEqual(('stadium-lantern',51.45,-2.60),(old['stadium_id'],old['latitude'],old['longitude']))
        self.assertEqual(('stadium-beacon',51.49,-2.57),(new['stadium_id'],new['latitude'],new['longitude']))
        self.assertEqual('Bristol',new['address']['locality']['town_city'])

    def test_season_fixture_isolation_and_neutral_ground(self):
        view=self.view(2023)
        self.assertEqual(['fixture-2023-home','fixture-2023-neutral'],[f['fixture_id'] for f in view['fixtures']])
        self.assertEqual('stadium-riverbank',view['fixtures'][1]['venue']['stadium_id'])
        self.assertNotIn('stadium-riverbank',[g['venue']['stadium_id'] for c in view['clubs'] for g in c['grounds']])

    def test_player_history_and_age(self):
        old=player_at_fixture(self.bundle,'player-alex','fixture-2022-home','2022-09-10T14:00:00Z')
        new=player_at_fixture(self.bundle,'player-alex','fixture-2023-home','2023-09-10T14:00:00Z')
        self.assertEqual((22,'club-harbour',6),(old['age'],old['affiliations'][0]['payload']['club_id'],old['affiliations'][0]['payload']['shirt_number']))
        self.assertEqual((23,'club-moor',8),(new['age'],new['affiliations'][0]['payload']['club_id'],new['affiliations'][0]['payload']['shirt_number']))

    def test_announced_move_not_effective_and_no_revision_resurrection(self):
        known=revisions_at(self.bundle['records'],'2023-06-20T00:00:00Z')
        self.assertTrue(active(known['home-harbour-lantern'],'2023-06-30T23:59:59Z'))
        self.assertFalse(active(known['home-harbour-beacon'],'2023-06-30T23:59:59Z'))
        self.assertFalse(active(known['home-harbour-lantern'],'2023-07-01T00:00:00Z'))
        self.assertTrue(active(known['home-harbour-beacon'],'2023-07-01T00:00:00Z'))
        self.assertFalse(active(known['alex-harbour'],'2023-07-01T00:00:00Z'))

    def test_future_knowledge_does_not_leak(self):
        known=revisions_at(self.bundle['records'],'2023-05-01T00:00:00Z')
        self.assertNotIn('home-harbour-beacon',known)
        self.assertIsNone(known['home-harbour-lantern']['valid_to'])
        self.assertIsNone(known['alex-harbour']['valid_to'])
        with self.assertRaises(ValueError):
            season_view(self.bundle,'season-2023','2023-09-01T00:00:00Z','2023-05-01T00:00:00Z')

    def test_date_outside_season_rejected(self):
        with self.assertRaises(ValueError):
            season_view(self.bundle,'season-2022','2024-01-01T00:00:00Z','2024-01-01T00:00:00Z')

    def test_invalid_connected_records(self):
        cases=[('season-2022','end_date','2021-01-01'),
               ('fixture-2023-home','stadium_id','stadium-lantern'),
               ('fixture-2023-home','scheduled_kickoff','2025-09-01T14:00:00Z'),
               ('fixture-2023-home','away_club_id','club-riverside'),
               ('club-harbour','location',{'town_city':'Bristol'}),
               ('stadium-lantern','latitude',200)]
        for rid,field,value in cases:
            with self.subTest(record=rid,field=field):
                bundle=copy.deepcopy(self.bundle)
                next(r for r in bundle['records'] if r['record_id']==rid)['payload'][field]=value
                self.assertTrue(validate(bundle))

    def test_duplicate_membership_identity_rejected(self):
        r=copy.deepcopy(next(r for r in self.bundle['records'] if r['record_id']=='member-2022-harbour'))
        r.update(record_id='duplicate-member',revision_id='duplicate-member-v1')
        self.bundle['records'].append(r)
        self.assertTrue(validate(self.bundle))

    def test_membership_must_cover_fixture_date(self):
        r=next(r for r in self.bundle['records'] if r['record_id']=='member-2023-moor')
        r['valid_to']='2023-09-01T00:00:00Z'
        self.assertTrue(validate(self.bundle))

    def test_membership_not_known_when_fixture_published(self):
        r=next(r for r in self.bundle['records'] if r['record_id']=='member-2023-moor')
        r['synthetic_released_at']='2023-07-01T00:00:00Z'
        self.assertTrue(validate(self.bundle))


if __name__=='__main__':
    unittest.main()
