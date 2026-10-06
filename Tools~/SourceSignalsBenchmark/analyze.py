import json,struct,csv,sys
from pathlib import Path
import numpy as np
from sklearn.ensemble import RandomForestClassifier
from sklearn.model_selection import GroupKFold
from sklearn.metrics import roc_auc_score,average_precision_score
root=Path(sys.argv[1]);names=json.loads((root/'source-feature-names.json').read_text())+['occlusion0.01','occlusion0.05','occlusion0.2']
def aggregate(case):
 rows=json.loads((root/('edges.json' if case=='reference' else 'simplify-edges.json')).read_text());b=(root/(case+'-projected.bin')).read_bytes();nr,nc=struct.unpack_from('<2i',b);q=np.frombuffer(b,'<f4',nr*nc,8).reshape(nr,nc).astype(float)
 features=np.zeros((len(rows),len(names)*4+3));columns=[]
 for name in names:columns.extend([name+'_mean',name+'_maxAbs',name+'_std',name+'_sideDifference'])
 columns+=['projectionDistanceMax','normalMismatchMean','normalMismatchMax']
 for e in range(len(rows)):
  vals=q[q[:,0]==e];assert len(vals)==6 and np.isfinite(vals[:,5]).all();v=vals[:,9:];result=[]
  for k,name in enumerate(names):
   f=v[:,k];side=abs(f[vals[:,1]==0].mean()-f[vals[:,1]==1].mean());result.extend([f.mean(),np.abs(f).max(),f.std(),side])
  features[e]=result+[vals[:,5].max(),(1-vals[:,6]).mean(),(1-vals[:,6]).max()]
 stats={'samples':nr,'fallback':int((q[:,4]==1).sum()),'misses':int((q[:,3]<0).sum()),'distanceMedian':float(np.median(q[:,5])),'distanceP95':float(np.quantile(q[:,5],.95)),'distanceMax':float(q[:,5].max()),'sourceNormalDotMedian':float(np.median(q[:,6])),'sourceNormalDotP05':float(np.quantile(q[:,6],.05))}
 print('PROJECTION',case,stats)
 return rows,features,columns,stats
rows,x,columns,refStats=aggregate('reference');simple,sx,_,simpleStats=aggregate('simplify');low=np.array([[r['lowAngle'],r['length']] for r in rows]);xyz=np.array([[r['midX'],r['midY'],r['midZ']] for r in rows]);weights=np.array([r['length'] for r in rows]);allx=np.column_stack((low,x));allcols=['lowAngle','length']+columns
# Spatial blocks hold neighbouring edges out together. Same geometry in both
# references is never treated as an independent asset train/test split.
voxel=np.floor((xyz-xyz.min(0))/(xyz.max(0)-xyz.min(0)+1e-12)*5).astype(int);_,groups=np.unique(voxel,axis=0,return_inverse=True);cv=list(GroupKFold(5).split(allx,groups=groups))
print('BLOCKS',len(np.unique(groups)),[len(test) for _,test in cv])
ao=[i for i,c in enumerate(allcols) if c.startswith('occlusion')];curvature=[i for i,c in enumerate(allcols) if c.startswith(('signedH','absH','cavity','normalSpread'))];source=list(range(2,len(allcols)));families={'low_geometry':[0,1],'low_plus_AO':[0,1]+ao,'low_plus_curvature':[0,1]+curvature,'projected_source':source,'low_plus_source':list(range(len(allcols)))};models={};results=[];single=[]
for label in ['hard','seamV1','seamV2']:
 y=np.array([r[label] for r in rows]);print('LABEL',label,'positive',y.sum(),'edge length share',weights[y==1].sum()/weights.sum())
 for j,name in enumerate(allcols):
  score=allx[:,j];auc=roc_auc_score(y,score);direction=1 if auc>=.5 else -1;auc=max(auc,1-auc);single.append({'label':label,'feature':name,'direction':direction,'aucExploratory':auc,'lengthWeightedAucExploratory':roc_auc_score(y,score*direction,sample_weight=weights),'positiveMedian':float(np.median(score[y==1])),'negativeMedian':float(np.median(score[y==0]))})
 for family,col in families.items():
  oof=np.zeros(len(y));folds=[]
  for fold,(train,test) in enumerate(cv):
   model=RandomForestClassifier(n_estimators=180,max_depth=6,min_samples_leaf=12,max_features=.7,class_weight='balanced',random_state=73,n_jobs=1);model.fit(allx[train][:,col],y[train]);oof[test]=model.predict_proba(allx[test][:,col])[:,1]
   if len(np.unique(y[test]))==2:folds.append(roc_auc_score(y[test],oof[test]))
  row={'label':label,'family':family,'rocAuc':roc_auc_score(y,oof),'averagePrecision':average_precision_score(y,oof),'lengthWeightedAuc':roc_auc_score(y,oof,sample_weight=weights),'foldMinAuc':min(folds),'foldMaxAuc':max(folds)};results.append(row);models[(label,family)]=oof;print('CV',row)
np.savez(root/'prediction-scores.npz',**{label+'_'+family:score for (label,family),score in models.items()})
for label in ['hard','seamV1','seamV2']:
 print('TOP_SINGLE',label,sorted([r for r in single if r['label']==label],key=lambda r:r['aucExploratory'],reverse=True)[:6])
for name,data in [('signal-cv.csv',results),('signal-single.csv',single)]:
 with (root/name).open('w',newline='') as w:writer=csv.DictWriter(w,fieldnames=list(data[0]));writer.writeheader();writer.writerows(data)
# Full per-edge features remain local geometry-derived captures; summary CSVs can
# be published without the user's geometry/texture data.
with (root/'edge-features.csv').open('w',newline='') as w:
 writer=csv.writer(w);metadata=[c for c in rows[0] if c not in allcols];writer.writerow(metadata+allcols)
 for r,f in zip(rows,allx):writer.writerow([r[c] for c in metadata]+list(f))
summary={'projectionReference':refStats,'projectionSimplify':simpleStats,'spatialBlocks':int(len(np.unique(groups))),'edgeCount':len(rows),'actualSimplifyEdges':len(simple),'labelCounts':{label:sum(r[label] for r in rows) for label in ['hard','seamV1','seamV2']},'cv':results}
(root/'summary.json').write_text(json.dumps(summary,indent=2))
