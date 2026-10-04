import json,csv,sys
from pathlib import Path
import numpy as np
from sklearn.metrics import roc_auc_score,average_precision_score
from sklearn.model_selection import GroupKFold
from sklearn.ensemble import RandomForestClassifier
root=Path(sys.argv[1]);rows=list(csv.DictReader((root/'edge-features.csv').open()));columns=list(rows[0]);metadata={'edge','a','b','f0','f1','midX','midY','midZ','hard','seamV1','seamV2','lowAngle','length'};names=['lowAngle','length']+[c for c in columns if c not in metadata];x=np.array([[float(r[c]) for c in names] for r in rows]);xyz=np.array([[float(r[c]) for c in ['midX','midY','midZ']] for r in rows]);voxel=np.floor((xyz-xyz.min(0))/(xyz.max(0)-xyz.min(0)+1e-12)*5).astype(int);_,groups=np.unique(voxel,axis=0,return_inverse=True);pred=np.load(root/'prediction-scores.npz');rng=np.random.default_rng(314);unique=np.unique(groups);lookup={g:i for i,g in enumerate(unique)};groupix=np.array([lookup[g] for g in groups]);boot=[];top=[]
for label in ['hard','seamV1','seamV2']:
 y=np.array([int(r[label]) for r in rows]);base=pred[label+'_low_geometry'];improved=pred[label+'_low_plus_source'];deltas=[];ap=[]
 for repeat in range(500):
  blockCounts=np.bincount(rng.integers(0,len(unique),len(unique)),minlength=len(unique));w=blockCounts[groupix];deltas.append(roc_auc_score(y,improved,sample_weight=w)-roc_auc_score(y,base,sample_weight=w));ap.append(average_precision_score(y,improved,sample_weight=w)-average_precision_score(y,base,sample_weight=w))
 r={'label':label,'deltaAuc':roc_auc_score(y,improved)-roc_auc_score(y,base),'deltaAucP025':float(np.quantile(deltas,.025)),'deltaAucP975':float(np.quantile(deltas,.975)),'deltaAp':average_precision_score(y,improved)-average_precision_score(y,base),'deltaApP025':float(np.quantile(ap,.025)),'deltaApP975':float(np.quantile(ap,.975))};boot.append(r);print('BOOTSTRAP',r)
 k=y.sum()
 for family in ['low_geometry','low_plus_AO','low_plus_curvature','low_plus_source']:
  score=pred[label+'_'+family];selected=np.argsort(score)[-k:];matched=int(y[selected].sum());r={'label':label,'family':family,'selected':int(k),'matched':matched,'precisionAtK':matched/k};top.append(r);print('TOP_K',r)
subset=np.array([int(r['hard'])==0 for r in rows]);sx=x[subset];sg=groups[subset];curvature=[i for i,c in enumerate(names) if c.startswith(('signedH','absH','cavity','normalSpread'))];ao=[i for i,c in enumerate(names) if c.startswith('occlusion')];families={'low_geometry':[0,1],'low_plus_AO':[0,1]+ao,'low_plus_curvature':[0,1]+curvature,'low_plus_source':list(range(len(names)))};soft=[]
for label in ['seamV1','seamV2']:
 y=np.array([int(r[label]) for r in rows])[subset]
 for family,col in families.items():
  oof=np.zeros(len(y))
  for train,test in GroupKFold(5).split(sx,groups=sg):
   model=RandomForestClassifier(n_estimators=180,max_depth=6,min_samples_leaf=12,max_features=.7,class_weight='balanced',random_state=73,n_jobs=1);model.fit(sx[train][:,col],y[train]);oof[test]=model.predict_proba(sx[test][:,col])[:,1]
  r={'label':label,'family':family,'positive':int(y.sum()),'edgeCount':len(y),'rocAuc':roc_auc_score(y,oof),'averagePrecision':average_precision_score(y,oof)};soft.append(r);print('SOFT_ONLY',r)
for name,data in [('signal-bootstrap.csv',boot),('signal-top-k.csv',top),('signal-soft-seams.csv',soft)]:
 with (root/name).open('w',newline='') as f:w=csv.DictWriter(f,fieldnames=list(data[0]));w.writeheader();w.writerows(data)
for case in ['reference','simplify']:
 b=(root/(case+'-projected.bin')).read_bytes();import struct;nr,nc=struct.unpack_from('<2i',b);q=np.frombuffer(b,'<f4',nr*nc,8).reshape(nr,nc);print('QUALITY_TAIL',case,'distance>.02',int((q[:,5]>.02).sum()),'sourceNormalDot<0',int((q[:,6]<0).sum()),'dist>.02 OR dot<0',int(((q[:,5]>.02)|(q[:,6]<0)).sum()))
