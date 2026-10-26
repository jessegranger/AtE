import sys, struct, collections
import numpy as np
def load(fn):
    ents=[]; cur=None
    for line in open(fn):
        line=line.rstrip('\n')
        if line.startswith('#ENT'):
            _,i,T,gms,el=line.split('\t'); cur={'id':int(i),'T':int(T),'gms':int(gms),'el':float(el),'r':{}}; ents.append(cur)
        elif cur is not None and '\t' in line:
            l,h=line.split('\t'); cur['r'][l]=bytes.fromhex(h)
    return ents
ents=[e for e in load(sys.argv[1]) if e['el']>0]
mode=sys.argv[2] if len(sys.argv)>2 else 'real'
T0=ents[0]['T']; g0=ents[0]['gms']
x=[]
for e in ents:
    if mode=='real': x.append(e['gms']-e['el']*1000.0)
    else: x.append(T0+30.0*(e['gms']-g0)/1000.0 - e['el']*30.0)
x=np.array(x)
print(len(ents),'entities; x spread',x.max()-x.min())
labels=collections.Counter(l for e in ents for l in e['r'])
res=[]
for l,c in labels.items():
    if c<8: continue
    es=[(k,e) for k,e in enumerate(ents) if l in e['r']]
    n=min(len(e['r'][l]) for _,e in es)
    xs=x[[k for k,_ in es]]
    for fmt,w in (('i',4),('f',4),('q',8),('d',8)):
        for o in range(0,n-w+1,4 if w==4 else 8):
            try: v=np.array([struct.unpack_from('<'+fmt,e['r'][l],o)[0] for _,e in es],dtype=float)
            except Exception: continue
            if not np.all(np.isfinite(v)) or v.std()==0 or np.abs(v).max()>1e15: continue
            r=np.corrcoef(xs,v)[0,1]
            if abs(r)>float(sys.argv[3] if len(sys.argv)>3 else 0.995):
                a,b=np.polyfit(xs,v,1)
                res.append((abs(r),l,o,fmt,len(es),a,b))
res.sort(reverse=True)
for r in res[:int(sys.argv[4]) if len(sys.argv)>4 else 25]: print('r=%.5f %s+0x%X %s n=%d slope=%.4f icpt=%.1f'%r)
print(len(res),'total candidates')
