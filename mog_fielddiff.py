# compare every field of authored nodes/edges against the stock distribution
import struct, collections, sys

def u32(b,o): return struct.unpack_from('<I',b,o)[0]
def i32(b,o): return struct.unpack_from('<i',b,o)[0]

def graph0(p):
    b=open(p,'rb').read()
    gh=0x20+i32(b,0x20)
    S,n=(gh+0xc)+i32(b,gh+0xc),u32(b,gh+8)
    EA,ec=(gh+0x14)+i32(b,gh+0x14),u32(b,gh+0x10)
    return b,S,n,EA,ec

sb,sS,sn,sEA,sec = graph0(sys.argv[1])      # stock
nb,nS,nn,nEA,nec = graph0(sys.argv[2])      # authored

def report(name, stockb, stockBase, stockN, newb, newBase, newN, firstNew, stride):
    print(f'\n=== {name}: stock {stockN}, authored-new {newN-firstNew}, stride {stride:#x} ===')
    for off in range(0, stride, 4):
        sv = collections.Counter(u32(stockb, stockBase+j*stride+off) for j in range(stockN))
        nv = collections.Counter(u32(newb, newBase+j*stride+off) for j in range(firstNew, newN))
        # offsets vary by construction; only flag fields that look like flags/counts
        if len(sv) > 200: kind = 'offset/varied'
        else: kind = 'enum/count'
        novel = [v for v in nv if v not in sv]
        zeroInNew = nv.get(0, 0)
        zeroInStock = sv.get(0, 0)
        flag = ''
        if kind == 'enum/count' and novel: flag = f'  <-- values stock NEVER has: {novel[:6]}'
        if zeroInStock == 0 and zeroInNew: flag = f'  <-- ZERO in {zeroInNew} authored, never zero in stock'
        print(f'  +0x{off:02x} {kind:13s} stockDistinct={len(sv):5d} newDistinct={len(nv):4d}'
              f' stockZero={zeroInStock:5d} newZero={zeroInNew:4d}{flag}')

report('NODE', sb, sS, sn, nb, nS, nn, sn, 0x48)
report('EDGE', sb, sEA, sec, nb, nEA, nec, sec, 0x28)
