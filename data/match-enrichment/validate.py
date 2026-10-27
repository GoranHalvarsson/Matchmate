"""Offline consistency checks for one bounded research pack, not a product schema."""
import hashlib
import copy
import json
import re
from pathlib import Path

HERE = Path(__file__).parent
ROOT = HERE.parents[1]
def load():
    return json.loads((HERE/'brentford-chelsea-2026-09-18.json').read_text(encoding='utf-8'))
def require(condition, message):
    if not condition: raise ValueError(message)
def validate(pack, check_baseline=True):
    sources=json.loads((HERE/'sources.json').read_text(encoding='utf-8'))
    source_ids={s['id'] for s in sources}
    require(len(source_ids)==len(sources), 'duplicate source')
    for s in sources:
        require(s['url'].startswith('https://') and s['observed_on']=='2026-10-17', 'source provenance')
        require(s['publication_date'] is None or s['publication_date']<=s['observed_on'], 'publication after observation')
    citation_keys={'evidence','corroboration','role_evidence','match_context_evidence','coverage_evidence','match_participation_evidence','report_evidence','club_report_evidence'}
    def citation(value):
        if isinstance(value,list):
            require(bool(value), 'empty citation list')
            for item in value: citation(item)
            return
        require(isinstance(value,dict) and value.get('source') in source_ids
                and isinstance(value.get('locator'),str) and bool(value['locator'].strip()),
                'unknown source or missing locator')
    def walk(value):
        if isinstance(value,dict):
            if 'source' in value:
                citation(value)
            for key,v in value.items():
                if key in citation_keys: citation(v)
                walk(v)
        elif isinstance(value,list):
            for v in value: walk(v)
    walk(pack)
    lineup_statistics=pack.get('lineup_statistics_audit',{})
    pack=copy.deepcopy(pack)
    pack.pop('lineup_statistics_audit',None)
    pre_statistics=copy.deepcopy(pack)
    f=pack['fixture']
    require(pack['id']=='premier-league:2026-27:2026-09-18:brentford:chelsea', 'fixture identity')
    require(f['played_date']['value']=='2026-09-18' and f['season']['value']=='2026/27' and f['result']['status']=='completed', 'fixture date/season/status')
    require(f['home']['value']=='brentford' and f['away']['value']=='chelsea', 'home-away identity')
    for fact in f.values(): require('evidence' in fact, 'fixture fact missing evidence')
    require(f['kickoff']['actual'] is None, 'actual kickoff unestablished')
    require(pack['temporal_semantics']['known_then_reconstructed'] is False, 'not a known-then reconstruction')
    people={p['id']:p for p in pack['players']}
    require(len(people)==len(pack['players'])==40, 'unique player roster')
    public_names=set()
    for p in people.values():
        require('evidence' in p and not p['global_identity_resolved'], 'source-printed identity required')
        identity=p.get('identity_resolution',{})
        require(identity.get('status')=='source_linked_public_name' and bool(identity.get('public_name','').strip())
                and 'evidence' in identity and 'match_participation_evidence' in identity,
                'missing public-name mapping or identity evidence')
        require(identity['public_name'] not in public_names, 'duplicate public-name mapping')
        public_names.add(identity['public_name'])
    def minute(t):
        require(t['seconds'] is None and t['precision']=='reported_minute', 'unsupported time precision')
        require(re.fullmatch(r'\d+(?:\+\d+)?',t['printed']) is not None, 'time label')
        parts=[int(x) for x in t['printed'].split('+')]
        require(t['minute']==parts[0] and t['added_minute']==(parts[1] if len(parts)>1 else None), 'time label mismatch')
        return parts[0]+(parts[1] if len(parts)>1 else 0)
    counts={}
    on_times={}; off_times={}
    for club,team in pack['teams'].items():
        require('evidence' in team, 'team evidence')
        require(len(team['starters'])==11 and len(team['bench'])==9, 'XI/bench count')
        roster=team['starters']+team['bench']
        require(len(set(roster))==20 and all(p in people and people[p]['club_id']==club for p in roster), 'roster identity')
        active=set(team['starters']); counts[club]=0
        for sub in sorted([s for s in pack['substitutions'] if s['club_id']==club],key=lambda s:minute(s['time'])):
            require('evidence' in sub, 'substitution evidence')
            m=minute(sub['time'])
            require(sub['off'] in active and sub['on'] in team['bench'] and sub['on'] not in on_times, 'invalid substitution on/off')
            active.remove(sub['off']); active.add(sub['on'])
            off_times[sub['off']]=m; on_times[sub['on']]=m; counts[club]+=1
    require(counts=={'chelsea':4,'brentford':5} and len(pack['substitutions'])==9, 'reported substitution counts')
    goals={club:0 for club in pack['teams']}
    for kind in ['goals','reported_bookings']:
        for event in pack[kind]:
            require('evidence' in event and event['player'] in people, 'event identity/evidence')
            p=event['player']; m=minute(event['time']); club=people[p]['club_id']
            require(p in pack['teams'][club]['starters'] or p in on_times, 'unused player event')
            require(m>=on_times.get(p,0) and m<=off_times.get(p,999), 'event outside playing interval at reported minute precision')
            if kind=='goals':
                require(event['club_id']==club, 'goal club mismatch'); goals[club]+=1
    require([goals[f['home']['value']],goals[f['away']['value']]]==f['result']['value']==[3,0], 'goals do not reconcile score')
    require(len(pack['reported_bookings'])==2, 'reported booking count')
    for key in ['penalties_awarded','penalties_taken','penalties_scored','red_cards','assists','event_seconds','player_positions_or_tracking','other_actual_officials','attendance_definition']:
        require(pack['coverage'][key] is None, 'unknown coverage must remain null: '+key)
    require(pack['coverage']['event_stream_complete'] is False, 'incomplete event coverage')
    for coach in pack['coach_contexts']:
        require('role_evidence' in coach and 'match_context_evidence' in coach and coach['matchday_physical_presence_verified'] is False, 'coach role/context boundary')
    require(not pack['rights']['product_display_or_replay_cleared'] and not pack['rights']['external_ai_reuse_cleared'], 'reuse not cleared')
    crosscheck=pack['second_report_crosscheck']
    categories={row['category']:row for row in crosscheck['categories']}
    require(len(categories)==len(crosscheck['categories'])==14, 'crosscheck category coverage')
    conflicts={row['id']:row for row in crosscheck['conflicts']}
    require(set(conflicts)=={'thiago-minute','chelsea-formation'}, 'required conflicts missing')
    for conflict in conflicts.values():
        require(conflict['status']=='unresolved' and conflict['canonical'] is None, 'unresolved conflict cannot have canonical value')
        require(categories[conflict['category']]['status']=='conflict'
                and categories[conflict['category']]['conflict_id']==conflict['id'], 'conflicting claim cannot be verified agreement')
        require(len(conflict['claims'])==2 and len({c['value'] for c in conflict['claims']})==2
                and all('evidence' in c for c in conflict['claims']), 'both conflicting source claims required')
    league=pack.get('league_record_assessment',{})
    require(all(key in league for key in ['goal_minute','formation_classifications','discipline','referee']), 'required league assessments missing')
    require(league.get('fixture_id')==pack['id'] and league.get('observed_on')=='2026-10-17', 'league fixture provenance')
    goal=league['goal_minute']
    require(goal['player']=='brentford:thiago' and goal['selected']=='83' and goal['status']=='selected_league_record'
            and bool(goal.get('rationale','').strip()) and 'evidence' in goal
            and goal['retained_disagreement']['value']=='80', 'goal league selection and retained disagreement required')
    forms={r['club_id']:r for r in league['formation_classifications']}
    require(len(forms)==len(league['formation_classifications'])==2 and set(forms)=={'chelsea','brentford'}, 'formation club coverage')
    for club,selected in [('chelsea','3-4-2-1'),('brentford','4-2-3-1')]:
        row=forms[club]
        require(row['selected']==selected and row['measured_tactical_truth'] is False
                and row['status']=='selected_league_classification_source_disagreement_unresolved'
                and bool(row['retained_disagreements']) and 'evidence' in row, 'formation classification is not measured truth')
    require([r['value'] for r in forms['brentford']['retained_disagreements']]==['4-3-3','4-3-3'], 'both club formation disagreements retained')
    discipline=league['discipline']
    require(discipline['complete_card_audit'] is False and discipline['other_cards_total'] is None, 'listed cards are not complete coverage')
    cards=discipline['cards']
    require(len(cards)==2 and {(r['player'],r['minute'],r['classification']) for r in cards}=={
        ('brentford:thiago',84,'yellow_card'),('brentford:sangare',85,'yellow_card')}
        and all('evidence' in r for r in cards), 'league card identities/minutes mismatch')
    require({(r['player'],r['time']['minute']) for r in pack['reported_bookings']}=={(r['player'],r['minute']) for r in cards}, 'cards do not match original bookings')
    referee=league['referee']
    require(referee['league_printed_name']=='Andy Madley' and referee['existing_report_name']=='Andrew Madley'
            and referee['legal_identity_resolved'] is False and 'evidence' in referee and 'report_evidence' in referee,
            'referee source-name variant boundary')
    timeline=pack.get('timeline_audit',{})
    require(timeline.get('fixture_id')==pack['id'] and timeline.get('observed_on')=='2026-10-17', 'timeline identity/provenance')
    require(timeline.get('complete_event_feed_verified') is False, 'displayed timeline is not complete feed')
    require(timeline.get('known_then_reconstructed') is False and timeline.get('same_minute_substitution_sequence')=='unknown; source display order is not on-field sequence', 'timeline temporal/sequence boundary')
    boundary=timeline['inspection_boundary']
    require(boundary['opening_observed'] is True and boundary['end_observed'] is True and 'evidence' in boundary, 'timeline inspection boundaries')
    def audit_time(t):
        require(t.get('precision')=='reported_minute' and t.get('seconds') is None, 'timeline source time precision')
        require(isinstance(t.get('printed'),str) and re.fullmatch(r'\d+(?:\+\d+)?',t['printed']) is not None, 'timeline time label')
        parts=[int(x) for x in t['printed'].split('+')]
        require(t.get('minute')==parts[0] and t.get('added_minute')==(parts[1] if len(parts)>1 else None), 'timeline structured time mismatch')
        return t['printed']
    audit_goals=timeline['goals']
    expected_goals={('brentford:anthony','61'),('brentford:thiago','83'),('brentford:carvalho','90+4')}
    require(len(audit_goals)==3 and {(g['player'],audit_time(g['time'])) for g in audit_goals}==expected_goals, 'timeline goals missing or mismatched')
    for g in audit_goals:
        require('evidence' in g and 'evidence' in g['formal_assist'], 'timeline goal/assist evidence')
        assist=g['formal_assist']
        if g['player']=='brentford:anthony':
            require(assist['player']=='brentford:schuster' and assist['status']=='explicitly_credited', 'explicit Anthony assist required')
        else:
            require(assist['player'] is None and assist['status']=='not_explicit_in_inspected_goal_entry', 'formal assist cannot be inferred')
        require(g['attributes_semantics']=='source-reported descriptions, not coordinates or tracking', 'finish attribute scope')
    audit_subs=timeline['substitutions']
    sub_key=lambda s:(s['club_id'],s['on'],s['off'],audit_time(s['time']))
    require(len(audit_subs)==9 and {sub_key(s) for s in audit_subs}=={sub_key(s) for s in pack['substitutions']}, 'timeline substitution mismatch')
    require(all('evidence' in s and 'club_report_evidence' in s for s in audit_subs), 'timeline substitution citations')
    cards=timeline['listed_cards']
    require(len(cards)==2 and {(c['player'],audit_time(c['time']),c['classification']) for c in cards}=={
        ('brentford:thiago','84','yellow_card'),('brentford:sangare','85','yellow_card')}, 'timeline cards missing or mismatched')
    require(all('evidence' in c for c in cards), 'timeline card evidence')
    stoppage=timeline['stoppage_observations']
    require(len(stoppage)==4 and {(audit_time(o['time']),o['kind'],o['period'],o['minimum_added_minutes']) for o in stoppage}=={
        ('45','minimum_announced','first_half',1),('45+2','period_end','first_half',None),
        ('90','minimum_announced','second_half',5),('90+7','period_end','second_half',None)}, 'minimum announcements and period-end labels must stay distinct')
    require(all(o['exact_elapsed_seconds'] is None and 'evidence' in o for o in stoppage), 'no exact stoppage duration established')
    display_counts=timeline['observed_displayed_counts']
    require(all(display_counts[k]==v for k,v in {'goals':3,'substitutions':9,'yellow_card_groups':2,'red_card_groups':0,'penalty_mentions':0,'var_mentions':0}.items())
            and display_counts['semantics']=='counts in inspected displayed commentary only; not actual totals or full-feed completeness', 'display counts are scoped observations')
    decisions=timeline['decisions']
    require('evidence' in display_counts, 'display-count evidence required')
    require('evidence' in decisions and all(decisions[k] is False for k in ['penalty_award_entry_found_in_displayed_commentary','var_decision_entry_found_in_displayed_commentary','red_card_entry_found_in_displayed_commentary']), 'decision display findings must retain inspected scope')
    require(all(decisions[k] is None for k in ['actual_penalties_awarded','actual_var_decision_total','actual_red_card_total']), 'display absence cannot become actual zero')
    appeal=decisions['rejected_penalty_appeal']
    require(appeal['player']=='chelsea:barco' and appeal['time'] is None and 'evidence' in appeal, 'rejected appeal has unknown minute')
    prior=copy.deepcopy(pack)
    del prior['timeline_audit']
    prior_digest=hashlib.sha256(json.dumps(prior,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
    require(prior_digest==json.loads((HERE/'league-assessment-checkpoint.json').read_text(encoding='utf-8'))['sha256'], 'prior league assessment changed')
    pack=prior
    extension=copy.deepcopy(pack)
    del extension['league_record_assessment']
    extension_digest=hashlib.sha256(json.dumps(extension,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
    require(extension_digest==json.loads((HERE/'public-name-extension-checkpoint.json').read_text(encoding='utf-8'))['sha256'], 'prior public-name extension changed')
    original=copy.deepcopy(extension)
    del original['second_report_crosscheck']
    for p in original['players']: del p['identity_resolution']
    checkpoint=json.loads((HERE/'original-claims-checkpoint.json').read_text(encoding='utf-8'))
    digest=hashlib.sha256(json.dumps(original,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
    require(digest==checkpoint['sha256'], 'original source claims changed')
    audit=lineup_statistics
    require(audit.get('fixture_id')==pre_statistics['id'] and audit.get('observed_on')=='2026-10-17'
            and audit.get('known_then_reconstructed') is False, 'lineup/statistics audit provenance')
    rows=audit['lineup_rows']
    mappings={r['player']:r for r in rows}
    require(len(rows)==len(mappings)==40 and set(mappings)==set(people), 'complete unique forty-player shirt mapping required')
    expected_numbers={
        'brentford':'kelleher=1,kayode=33,schuster=44,ajer=20,lewis-potter=23,yarmoliuk=6,janelt=27,anthony=19,damsgaard=24,schade=7,thiago=9,hickey=2,henry=3,ouattara=11,wilson=13,carvalho=14,sangare=18,diouf=25,balcombe=31,fredrick=48',
        'chelsea':'martinez=1,fofana=3,lacroix=5,colwill=6,neto=7,henderson=14,barco=4,chavarria=29,palmer=10,rogers=17,welbeck=18,gittens=11,hato=21,quenda=23,gusto=27,nicoll-jazuli=32,acheampong=34,penders=39,estevao=41,lavia=45'}
    bench_categories={
        'brentford':'hickey=Defender,henry=Defender,ouattara=Forward,wilson=Forward,carvalho=Midfielder,sangare=Midfielder,diouf=Defender,balcombe=Goalkeeper,fredrick=Defender',
        'chelsea':'gittens=Forward,hato=Defender,quenda=Midfielder,gusto=Defender,nicoll-jazuli=Midfielder,acheampong=Defender,penders=Goalkeeper,estevao=Forward,lavia=Midfielder'}
    for club,encoded in expected_numbers.items():
        expected={club+':'+item.split('=')[0]:int(item.split('=')[1]) for item in encoded.split(',')}
        club_rows=[r for r in rows if r['club_id']==club]
        require(len(club_rows)==20 and len({r['shirt_number'] for r in club_rows})==20, 'shirt numbers unique within club')
        categories={club+':'+item.split('=')[0]:item.split('=')[1] for item in bench_categories[club].split(',')}
        for player,number in expected.items():
            r=mappings[player]
            require(r['club_id']==club and r['shirt_number']==number and type(r['shirt_number']) is int and 'evidence' in r, 'shirt-player source relation mismatch')
            is_bench=player in pre_statistics['teams'][club]['bench']
            require(r['roster_status']==('bench' if is_bench else 'starter') and r['broad_source_category']==categories.get(player), 'source category or roster status mismatch')
            require(r['individual_match_role'] is None and r['role_semantics']=='unknown; formation diagram is not measured position or individual-role evidence', 'individual tactical role cannot be inferred')
            require(r['category_semantics']==('broad label in match-page substitute list, not individual match tactical role' if is_bench else 'not explicitly labelled for starter in inspected view'), 'broad category semantics required')
    require(len(audit['captains'])==2 and {r['player'] for r in audit['captains']}=={'brentford:janelt','chelsea:colwill'}
            and all('evidence' in r for r in audit['captains']), 'captain source claims')
    metrics={r['id']:r for r in audit['metrics']}
    expected_metrics={'possession':('percent',40.2,59.8),'xg':('source_xg_units',2.65,.83),'total_shots':('count',13,12),'shots_on_target':('count',6,2),'big_chances':('count',3,0),'corners':('count',4,12),'saves':('count',2,3),'red_cards':('count',0,0),'yellow_cards':('count',2,0),'fouls':('count',10,7),'offsides':('count',2,2)}
    require(len(metrics)==len(audit['metrics'])==len(expected_metrics) and set(metrics)==set(expected_metrics), 'eleven accepted core metric pairs required')
    for key,(unit,home,away) in expected_metrics.items():
        r=metrics[key]
        require(r['unit']==unit and r['values']=={'brentford':home,'chelsea':away} and 'evidence' in r, 'metric source units or home-away values mismatch')
        require(r['scope']=='rendered completed-match team summary' and r['period_breakdown'] is None and r['operational_definition'] is None, 'metric period/definition scope unavailable')
    semantics=audit['statistics_semantics']
    require(semantics['team_order']==['brentford','chelsea'] and semantics['scope']=='rendered completed-match team summary'
            and semantics['period_selector_observed'] is False and semantics['certified_period_definition'] is None
            and semantics['time_series_or_cursor_snapshot'] is False and semantics['detailed_definitions_available'] is False
            and semantics['xg_model_or_method'] is None and semantics['reported_zero_is_source_claim_not_nonmention'] is True
            and 'evidence' in semantics, 'statistics source scope and explicit-zero semantics required')
    anomalies=audit['excluded_anomalies']
    require(len(anomalies)==2 and {(r['source_label'],r['printed_values']['brentford'],r['printed_values']['chelsea']) for r in anomalies}=={
        ('Shots Inside the Box',2,1),('Shots Outside the Box',4,4)} and all(r['status']=='excluded_unresolved_label_or_consistency' and 'evidence' in r for r in anomalies), 'anomalous shot-location rows must remain excluded and unrepaired')
    pre_statistics_hash=hashlib.sha256(json.dumps(pre_statistics,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
    require(pre_statistics_hash==json.loads((HERE/'timeline-audit-checkpoint.json').read_text(encoding='utf-8'))['sha256'], 'prior complete timeline pack changed')
    if check_baseline:
        for row in json.loads((HERE/'scope-baseline.json').read_text(encoding='utf-8-sig')):
            require(hashlib.sha256((ROOT/row['path']).read_bytes()).hexdigest()==row['sha256'], 'outside-scope file changed: '+row['path'])
    return {'starters':len([r for r in rows if r['roster_status']=='starter']),
            'bench':len([r for r in rows if r['roster_status']=='bench']),
            'substitutions':counts,'goals':goals,'reported_bookings':len(pack['reported_bookings']),
            'match_shirt_mappings':len(rows),'broad_bench_categories':sum(r['broad_source_category'] is not None for r in rows),
            'unknown_individual_match_roles':sum(r['individual_match_role'] is None for r in rows),
            'captains':len(audit['captains']),'core_metric_pairs':len(metrics),'excluded_anomalies':len(anomalies)}
if __name__=='__main__': print(json.dumps(validate(load()),indent=2))
