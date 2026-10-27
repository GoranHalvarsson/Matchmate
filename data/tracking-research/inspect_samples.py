"""Bounded primary-source inspection. Retains aggregates, never raw tracking files.
Run --fetch explicitly for public network reads; default checks the saved report.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import csv
import hashlib
import io
import json
import math
from pathlib import Path
import urllib.request
import urllib.parse

HERE=Path(__file__).parent
PINS={'skillcorner':'4340d274572876239c154c90bc507a9b3250a656',
      'metrica':'e706dd506b360d69d9d123d5b8026e7294b13996',
      'lastrow':'72129b58d0b866c21ab895d97b426333330f6294'}
REPOS={'skillcorner':'SkillCorner/opendata','metrica':'metrica-sports/sample-data',
       'lastrow':'Friends-of-Tracking-Data-FoTD/Last-Row'}
CAP=5_000_000
N=300

def require(value,message):
    if not value: raise ValueError(message)

class Reads:
    def __init__(self): self.bytes=Counter(); self.receipts=[]
    def get(self,source,path,lines=None,media=False):
        prefix='https://media.githubusercontent.com/media/' if media else 'https://raw.githubusercontent.com/'
        url=prefix+REPOS[source]+'/'+PINS[source]+'/'+urllib.parse.quote(path,safe='/')
        remaining=CAP-self.bytes[source]
        require(remaining>0,'source byte budget exhausted')
        with urllib.request.urlopen(url,timeout=30) as response:
            if lines is None:
                body=response.read(remaining+1)
                require(len(body)<=remaining,'source byte budget exceeded')
                complete=True
            else:
                parts=[]; consumed=0
                for _ in range(lines):
                    line=response.readline(remaining-consumed+1)
                    require(consumed+len(line)<=remaining,'source byte budget exceeded')
                    if not line:break
                    require(line.endswith(b'\n'),'incomplete capped line')
                    parts.append(line);consumed+=len(line)
                body=b''.join(parts); complete=False
            receipt={'source':source,'url':url,'resolved_url':response.url,'status':response.status,
                     'scope':'whole small file' if complete else f'first {len(parts)} complete lines only',
                     'bytes_hashed':len(body),'sha256':hashlib.sha256(body).hexdigest(),
                     'hash_semantics':'exact bytes returned to parser; not full LFS object hash' if not complete else 'exact full response bytes'}
        self.bytes[source]+=len(body);self.receipts.append(receipt)
        return body

def clock(label):
    h,m,s=label.split(':');return int(h)*3600+int(m)*60+float(s)

def extrema(points):
    return {'x':[min(x for x,y in points),max(x for x,y in points)],
            'y':[min(y for x,y in points),max(y for x,y in points)]} if points else None

def finite_number(value):
    """Missing/NaN source values stay missing; never convert them to zero."""
    if not value.strip(): return None
    parsed=float(value)
    return parsed if math.isfinite(parsed) else None

def skillcorner(reads):
    catalogue=json.loads(reads.get('skillcorner','data/matches.json'))
    match_id=1874553
    require(any(r['id']==match_id for r in catalogue),'selected match absent from catalogue')
    base=f'data/matches/{match_id}/{match_id}'
    meta=json.loads(reads.get('skillcorner',base+'_match.json'))
    pointer=reads.get('skillcorner',base+'_tracking_extrapolated.jsonl').decode()
    require(pointer.startswith('version https://git-lfs.github.com/spec/v1'),'expected actual LFS pointer')
    pointer_fields=dict(line.split(' ',1) for line in pointer.strip().splitlines())
    frames=[json.loads(line) for line in reads.get('skillcorner',base+'_tracking_extrapolated.jsonl',lines=N,media=True).splitlines()]
    require(len(frames)==N and len({f['frame'] for f in frames})==N,'tracking frame count/duplicates')
    roster={r['id']:r for r in meta['players']}
    require(len(roster)==len(meta['players']),'duplicate roster IDs')
    unknown=set(); duplicate_frames=[]; positions=[]; detected=Counter(); balls=Counter(); per_frame=[]; timestamps=[]
    finite_counts=[];missing_player_xy=0
    frame_ids=set();seen_ids=set()
    for f in frames:
        ids=[r['player_id'] for r in f['player_data']]
        if len(set(ids))!=len(ids):duplicate_frames.append(f['frame'])
        unknown.update(set(ids)-set(roster));seen_ids.update(ids);per_frame.append(len(ids));frame_ids.add(f['frame'])
        if f['timestamp'] is not None:timestamps.append(clock(f['timestamp']))
        valid_xy=0
        for r in f['player_data']:
            detected[str(r['is_detected'])]+=1
            if all(isinstance(r.get(k),(int,float)) and math.isfinite(r[k]) for k in ['x','y']):
                positions.append((r['x'],r['y']));valid_xy+=1
            else:missing_player_xy+=1
        finite_counts.append(valid_xy)
        b=f['ball_data'];present=all(isinstance(b.get(k),(int,float)) and math.isfinite(b[k]) for k in ['x','y'])
        balls['present_xy' if present else 'missing_xy']+=1
        if present:balls['detected' if b.get('is_detected') is True else 'extrapolated' if b.get('is_detected') is False else 'flag_unknown']+=1
    require(not unknown and not duplicate_frames,'unresolved or duplicate sampled player IDs')
    examples=[]
    selected_ids=[]
    for team_id in [meta['home_team']['id'],meta['away_team']['id']]:
        selected_ids.extend(sorted(i for i in seen_ids if roster[i]['team_id']==team_id)[:2])
    team_names={meta['home_team']['id']:meta['home_team']['name'],meta['away_team']['id']:meta['away_team']['name']}
    for player_id in selected_ids:
        r=roster[player_id]
        examples.append({'player_id':player_id,'public_name':(r['first_name'].strip()+' '+r['last_name'].strip()),
                         'team_id':r['team_id'],'team_name':team_names[r['team_id']],'shirt':r['number'],'locator':f'players[id={player_id}]; sampled player_data.player_id'})
    return {'match_id':match_id,'catalogue_count':len(catalogue),'readme_described_count':10,
            'fixture':{'date_time':meta['date_time'],'home':meta['home_team']['name'],'away':meta['away_team']['name'],
                       'competition':meta['competition_edition']['name'],'status':meta['status']},
            'pitch_metres':[meta['pitch_length'],meta['pitch_width']],'home_team_side':meta['home_team_side'],
            'roster_count':len(roster),'sample_frames':len(frames),'frame_range':[min(frame_ids),max(frame_ids)],
            'period_counts':dict(Counter(str(f['period']) for f in frames)),
            'timestamp_nonnull_count':len(timestamps),'timestamp_seconds_range':[min(timestamps),max(timestamps)],
            'timestamp_delta_counts':dict(Counter(str(round(b-a,5)) for a,b in zip(timestamps,timestamps[1:]))),
            'per_frame_player_count_distribution':dict(Counter(per_frame)),'sample_unique_player_ids':len(seen_ids),
            'per_frame_finite_player_xy_count_distribution':dict(Counter(finite_counts)),
            'missing_or_nonfinite_player_xy_observations':missing_player_xy,
            'unknown_player_ids':sorted(unknown),'duplicate_player_id_frames':duplicate_frames,
            'player_observation_flags':dict(detected),'ball_observation_counts':dict(balls),
            'player_observations_extrapolated_percent':round(100*detected['False']/sum(detected.values()),2),
            'player_xy_outside_nominal_pitch_observations':sum(abs(x)>meta['pitch_length']/2 or abs(y)>meta['pitch_width']/2 for x,y in positions),
            'sample_player_xy_range':extrema(positions),'identity_examples':examples,
            'lfs_pointer':{'object_sha256':pointer_fields['oid'].split(':')[1],'whole_object_size':int(pointer_fields['size']),
                           'whole_object_downloaded_or_hash_verified':False},
            'limits':'Prefix only; initial null frames included. Broadcast-estimated positions and extrapolations, not independently verified ground truth. No whole-game coverage or event alignment established.'}

def metrica(reads):
    base='data/Sample_Game_1/Sample_Game_1_'
    teams={}; frame_sets={}; ids={}; ball_maps={}
    for team in ['Home','Away']:
        raw=reads.get('metrica',base+'RawTrackingData_'+team+'_Team.csv',lines=N+3)
        rows=list(csv.reader(io.StringIO(raw.decode())))
        header=rows[2]; data=rows[3:];require(len(data)==N,'Metrica frame count')
        player_columns=[(i,name) for i,name in enumerate(header) if name.startswith('Player')]
        require(len({name for i,name in player_columns})==len(player_columns),'Metrica duplicate player header')
        ids[team]={name for i,name in player_columns};points=[];counts=[];ball_maps[team]={}; times=[];frame_ids=[]
        ball_index=header.index('Ball')
        for row in data:
            frame=int(row[1]);frame_ids.append(frame);times.append(float(row[2]));present=0
            for i,name in player_columns:
                x,y=float(row[i]),float(row[i+1])
                if math.isfinite(x) and math.isfinite(y):points.append((x,y));present+=1
            counts.append(present)
            ball_maps[team][frame]=(float(row[ball_index]),float(row[ball_index+1]))
        frame_sets[team]=set(frame_ids)
        require(len(set(frame_ids))==N,'Metrica duplicate frames')
        teams[team]={'anonymous_player_columns':len(player_columns),'column_ids':sorted(ids[team]),
                     'sample_frames':N,'frame_range':[min(frame_ids),max(frame_ids)],'periods':sorted({int(r[0]) for r in data}),
                     'time_seconds_range':[min(times),max(times)],'delta_counts':dict(Counter(str(round(b-a,5)) for a,b in zip(times,times[1:]))),
                     'present_player_count_distribution':dict(Counter(counts)),'sample_normalized_xy_range':extrema(points),
                     'missing_ball_xy_frames':sum(not all(math.isfinite(v) for v in xy) for xy in ball_maps[team].values())}
    events=list(csv.DictReader(io.StringIO(reads.get('metrica',base+'RawEventsData.csv').decode())))
    relevant=[e for e in events if int(e['Start Frame'])<=N and (int(e['End Frame'])>=1 or int(e['End Frame'])==0)]
    unknown=sorted({e[k] for e in relevant for k in ['From','To'] if e[k] and e[k] not in ids[e['Team']]})
    require(frame_sets['Home']==frame_sets['Away'] and not unknown,'Metrica frame/anonymous-event identity join')
    return {'sample':'Sample Game 1','identity':'anonymised; no actual player/team/competition names', 'teams':teams,
            'shared_frame_count':len(frame_sets['Home']&frame_sets['Away']),
            'same_ball_coordinates_on_shared_frames':sum(ball_maps['Home'][f]==ball_maps['Away'][f] for f in frame_sets['Home']),
            'whole_small_event_file_rows':len(events),'event_rows_overlapping_sample':len(relevant),
            'event_rows_fully_contained_with_positive_end_frame':sum(int(e['Start Frame'])>=1 and 1<=int(e['End Frame'])<=N for e in relevant),
            'overlap_event_rows_with_zero_end_frame':sum(int(e['End Frame'])==0 for e in relevant),
            'event_type_counts_in_overlap':dict(Counter(e['Type'] for e in relevant)),
            'unknown_event_player_ids_in_overlap':unknown,
            'event_start_time_matches_frame_25fps':all(abs(float(e['Start Time [s]'])-int(e['Start Frame'])/25)<.00001 for e in relevant),
            'coordinate_semantics':'README: normalized 0..1 top-left origin, 105x68 metres; actual prefix ranges reported without clamping.',
            'limits':'First 300 frames per team only, event alignment checked only for overlapping rows. Not Premier League or named-player evidence; reuse basis not comprehensively established.'}

def lastrow(reads):
    path='datasets/positional_data/LIV_[2]-1_CHE.csv'
    raw=reads.get('lastrow',path);rows=list(csv.DictReader(io.StringIO(raw.decode())))
    frame_players=defaultdict(list);points=[];present=Counter();missing_xy=0
    for r in rows:
        frame=int(r['frame']);frame_players[frame].append(r['player'])
        xy=tuple(finite_number(r[k]) for k in ['x','y'])
        if all(v is not None for v in xy):points.append(xy);present[frame]+=1
        else:missing_xy+=1
    duplicates=[f for f,players in frame_players.items() if len(players)!=len(set(players))]
    require(not duplicates,'Last Row duplicate frame/local-player pair')
    return {'sample':path,'scope':'whole small selected play file; not main 19-play collection','rows':len(rows),
            'frames':len(frame_players),'frame_range':[min(frame_players),max(frame_players)],
            'local_ids':len({r['player'] for r in rows}),'objects_per_frame_distribution':dict(Counter(len(v) for v in frame_players.values())),
            'present_xy_objects_per_frame_distribution':dict(Counter(present.get(f,0) for f in frame_players)),
            'missing_xy_rows':missing_xy,'missing_z_rows':sum(not r['z'].strip() for r in rows),
            'duplicate_frame_local_id_pairs':duplicates,'nonnull_name_rows':sum(bool(r['name'].strip()) for r in rows),
            'nonnull_team_rows':sum(bool(r['team'].strip()) for r in rows),
            'nonnull_shirt_rows':sum(bool(r['player_num'].strip() or r['num'].strip()) for r in rows),
            'partial_identity_examples':[dict(zip(['player','team','shirt'],key)) for key in sorted({(r['player'],r['team'],r['player_num'] or r['num']) for r in rows if r['player_num'].strip() or r['num'].strip()})],
            'sample_xy_range':extrema(points),'nonzero_z_local_ids':sorted({r['player'] for r in rows if finite_number(r['z']) not in [None,0]}),
            'time_semantics':'README 20 fps; frame-local span derived as (last-first)/20, not match-clock timestamps',
            'derived_frame_span_seconds':(max(frame_players)-min(frame_players))/20,
            'limits':'Selected filename refers to Liverpool-Chelsea goal play, not verified match identity/date or player names. Name column is blank; team and two partial shirt references are not a verified public-name roster join. Object/ball roles not independently resolved. Authors warn accuracy is unsuitable for research and players can be missing.'}

def fetch():
    reads=Reads();report={'observed_on':datetime.now(timezone.utc).date().isoformat(),'pins':PINS,'frame_target':N,'per_source_parser_byte_cap':CAP,
                        'no_raw_dataset_retained':True,'application_imported':False}
    for source in REPOS:
        reads.get(source,'README.md')
        if source=='skillcorner':reads.get(source,'LICENSE')
    report['skillcorner']=skillcorner(reads);report['metrica']=metrica(reads);report['lastrow']=lastrow(reads)
    report['receipts']=reads.receipts;report['bytes_returned_to_parser_by_source']=dict(reads.bytes)
    check(report)
    (HERE/'inspection-summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return report

def check(report):
    require(report['no_raw_dataset_retained'] is True and report['application_imported'] is False,'research scope')
    require(all(n<=CAP for n in report['bytes_returned_to_parser_by_source'].values()),'source cap')
    require(all(len(r['sha256'])==64 and r['bytes_hashed']>0 and r['status']==200 for r in report['receipts']),'receipt integrity')
    require(all(PINS[r['source']] in r['url'] for r in report['receipts']),'unpinned source')
    require(report['skillcorner']['sample_frames']==N and report['metrica']['shared_frame_count']==N,'bounded sample counts')
    require(not report['skillcorner']['unknown_player_ids'] and not report['skillcorner']['duplicate_player_id_frames'],'SkillCorner joins')
    require(report['metrica']['event_start_time_matches_frame_25fps'] and not report['metrica']['unknown_event_player_ids_in_overlap'],'Metrica alignment')

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--fetch',action='store_true');args=parser.parse_args()
    result=fetch() if args.fetch else json.loads((HERE/'inspection-summary.json').read_text(encoding='utf-8'))
    check(result)
    print(json.dumps({key:result[key] for key in ['skillcorner','metrica','lastrow','bytes_returned_to_parser_by_source']},indent=2,ensure_ascii=False))
