// binksub: re-encode the bottom rows of selected Bink 1 ('i') frames as FILL/RAW blocks,
// copying everything else bit-exactly. Used to burn subtitles into Bink videos.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include "ref/binkdata.h"

static int VERSION;
/* ---------------- bit reader / writer (LSB first) ---------------- */
typedef struct { const uint8_t *b; long n; long p; } BR;
static inline int rbit(BR *r) { if (r->p >= r->n * 8) { r->p++; return 0; } int v = (r->b[r->p >> 3] >> (r->p & 7)) & 1; r->p++; return v; }
static inline unsigned rbits(BR *r, int n) { unsigned v = 0; for (int i = 0; i < n; i++) v |= (unsigned)rbit(r) << i; return v; }

typedef struct { uint8_t *b; long cap; long p; } BW;
static void wbit(BW *w, int v) {
    if ((w->p >> 3) >= w->cap) { long nc = w->cap * 2 + 1024; w->b = realloc(w->b, nc); memset(w->b + w->cap, 0, nc - w->cap); w->cap = nc; }
    if (v) w->b[w->p >> 3] |= 1 << (w->p & 7);
    w->p++;
}
static void wbits(BW *w, unsigned v, int n) { for (int i = 0; i < n; i++) wbit(w, (v >> i) & 1); }
static long G_SRCBASE = -1; static long *G_OPS = 0; static long G_NOPS = 0, G_OPCAP = 0;
static void wcopy(BW *w, const uint8_t *src, long from, long to) {
    if (G_SRCBASE >= 0 && to > from) {
        long ob = w->p, sb = G_SRCBASE * 8 + from, len = to - from;
        if (G_NOPS && G_OPS[3*(G_NOPS-1)] + G_OPS[3*(G_NOPS-1)+2] == ob && G_OPS[3*(G_NOPS-1)+1] + G_OPS[3*(G_NOPS-1)+2] == sb) G_OPS[3*(G_NOPS-1)+2] += len;
        else { if (G_NOPS == G_OPCAP) { G_OPCAP = G_OPCAP * 2 + 4096; G_OPS = realloc(G_OPS, sizeof(long) * 3 * G_OPCAP); } G_OPS[3*G_NOPS] = ob; G_OPS[3*G_NOPS+1] = sb; G_OPS[3*G_NOPS+2] = len; G_NOPS++; }
    }
    for (long p = from; p < to; p++) wbit(w, (src[p >> 3] >> (p & 7)) & 1);
}

/* ---------------- huffman ---------------- */
typedef struct { int vlc; uint8_t syms[16]; } Tree;
static int huff_read(BR *r, Tree *t) {
    unsigned code = 0;
    for (int len = 1; len <= 7; len++) {
        code |= (unsigned)rbit(r) << (len - 1);
        for (int i = 0; i < 16; i++)
            if (bink_tree_lens[t->vlc][i] == len && bink_tree_bits[t->vlc][i] == code) return t->syms[i];
    }
    fprintf(stderr, "bad huffman code\n"); exit(2);
}
static void huff_write(BW *w, Tree *t, int v) {
    for (int i = 0; i < 16; i++) if (t->syms[i] == v) { wbits(w, bink_tree_bits[t->vlc][i], bink_tree_lens[t->vlc][i]); return; }
    fprintf(stderr, "symbol not in tree\n"); exit(2);
}
static void merge(BR *r, uint8_t *dst, uint8_t *src, int size) {
    uint8_t *src2 = src + size; int size2 = size;
    do { if (!rbit(r)) { *dst++ = *src++; size--; } else { *dst++ = *src2++; size2--; } } while (size && size2);
    while (size--) *dst++ = *src++;
    while (size2--) *dst++ = *src2++;
}
static void read_tree(BR *r, Tree *t) {
    uint8_t tmp1[16] = {0}, tmp2[16], *in = tmp1, *out = tmp2;
    t->vlc = rbits(r, 4);
    if (!t->vlc) { for (int i = 0; i < 16; i++) t->syms[i] = i; return; }
    if (rbit(r)) {
        int len = rbits(r, 3);
        for (int i = 0; i <= len; i++) { t->syms[i] = rbits(r, 4); tmp1[t->syms[i]] = 1; }
        for (int i = 0; i < 16 && len < 15; i++) if (!tmp1[i]) t->syms[++len] = i;
    } else {
        int len = rbits(r, 2);
        for (int i = 0; i < 16; i++) in[i] = i;
        for (int i = 0; i <= len; i++) {
            int size = 1 << i;
            for (int tt = 0; tt < 16; tt += size << 1) merge(r, out + tt, in + tt, size);
            uint8_t *x = in; in = out; out = x;
        }
        memcpy(t->syms, in, 16);
    }
}

/* ---------------- bundles ---------------- */
enum { B_TYPES, B_SUB, B_COLORS, B_PATTERN, B_XOFF, B_YOFF, B_IDC, B_DDC, B_RUN, NB };
typedef struct { int len; Tree tree; int *data; int dec; int ptr; int dead; } Bundle;
typedef struct {
    Bundle b[NB]; Tree col_high[16]; int col_last;
} Ctx;

static int av_log2i(unsigned v) { int n = 0; while (v >>= 1) n++; return n; }
static void init_lengths(Ctx *c, int width, int bw) {
    width = (width + 7) & ~7;
    c->b[B_TYPES].len = av_log2i((width >> 3) + 511) + 1;
    c->b[B_SUB].len = av_log2i((width >> 4) + 511) + 1;
    c->b[B_COLORS].len = av_log2i(bw * 64 + 511) + 1;
    c->b[B_IDC].len = c->b[B_DDC].len = c->b[B_XOFF].len = c->b[B_YOFF].len = av_log2i((width >> 3) + 511) + 1;
    c->b[B_PATTERN].len = av_log2i((bw << 3) + 511) + 1;
    c->b[B_RUN].len = av_log2i(bw * 48 + 511) + 1;
}
static int ready(Bundle *b) { return !b->dead && b->dec <= b->ptr; }
#define CHECKREAD(b, t) if (!ready(b)) return; t = rbits(r, (b)->len); if (!t) { (b)->dead = 1; return; }

static const int rlelens[4] = {4, 8, 12, 32};
static void rd_types(BR *r, Bundle *b) {
    int t, last = 0; CHECKREAD(b, t);
    int end = b->dec + t;
    if (rbit(r)) { int v = rbits(r, 4); while (b->dec < end) b->data[b->dec++] = v; }
    else while (b->dec < end) {
        int v = huff_read(r, &b->tree);
        if (v < 12) { last = v; b->data[b->dec++] = v; }
        else { int run = rlelens[v - 12]; for (int k = 0; k < run; k++) b->data[b->dec++] = last; }
    }
}
static void rd_colors(BR *r, Bundle *b, Ctx *c) {
    int t; CHECKREAD(b, t);
    int end = b->dec + t;
    if (rbit(r)) { c->col_last = huff_read(r, &c->col_high[c->col_last]); int v = (c->col_last << 4) | huff_read(r, &b->tree); while (b->dec < end) b->data[b->dec++] = v; }
    else while (b->dec < end) { c->col_last = huff_read(r, &c->col_high[c->col_last]); int v = (c->col_last << 4) | huff_read(r, &b->tree); b->data[b->dec++] = v; }
}
static void rd_patterns(BR *r, Bundle *b) {
    int t; CHECKREAD(b, t);
    int end = b->dec + t;
    while (b->dec < end) { int v = huff_read(r, &b->tree); v |= huff_read(r, &b->tree) << 4; b->data[b->dec++] = v; }
}
static void rd_motion(BR *r, Bundle *b) {
    int t; CHECKREAD(b, t);
    int end = b->dec + t;
    if (rbit(r)) { int v = rbits(r, 4); if (v && rbit(r)) v = -v; while (b->dec < end) b->data[b->dec++] = v; }
    else while (b->dec < end) { int v = huff_read(r, &b->tree); if (v && rbit(r)) v = -v; b->data[b->dec++] = v; }
}
static void rd_runs(BR *r, Bundle *b) {
    int t; CHECKREAD(b, t);
    int end = b->dec + t;
    if (rbit(r)) { int v = rbits(r, 4); while (b->dec < end) b->data[b->dec++] = v; }
    else while (b->dec < end) b->data[b->dec++] = huff_read(r, &b->tree);
}
static void rd_dcs(BR *r, Bundle *b, int start_bits, int has_sign) {
    int len; CHECKREAD(b, len);
    int v = rbits(r, start_bits - has_sign);
    if (v && has_sign && rbit(r)) v = -v;
    b->data[b->dec++] = v; len--;
    for (int i = 0; i < len; i += 8) {
        int len2 = len - i < 8 ? len - i : 8;
        int bsize = rbits(r, 4);
        if (bsize) for (int j = 0; j < len2; j++) { int v2 = rbits(r, bsize); if (v2 && rbit(r)) v2 = -v2; v += v2; b->data[b->dec++] = v; }
        else for (int j = 0; j < len2; j++) b->data[b->dec++] = v;
    }
}
static int getv(Bundle *b) { return b->data[b->ptr++]; }

static void skip_dct(BR *r) {
    int coef_list[128], mode_list[128];
    int list_start = 64, list_end = 64, list_pos, bits, ccoef, mode;
    coef_list[list_end] = 4; mode_list[list_end++] = 0;
    coef_list[list_end] = 24; mode_list[list_end++] = 0;
    coef_list[list_end] = 44; mode_list[list_end++] = 0;
    coef_list[list_end] = 1; mode_list[list_end++] = 3;
    coef_list[list_end] = 2; mode_list[list_end++] = 3;
    coef_list[list_end] = 3; mode_list[list_end++] = 3;
    for (bits = (int)rbits(r, 4) - 1; bits >= 0; bits--) {
        list_pos = list_start;
        while (list_pos < list_end) {
            if (!(mode_list[list_pos] | coef_list[list_pos]) || !rbit(r)) { list_pos++; continue; }
            ccoef = coef_list[list_pos]; mode = mode_list[list_pos];
            switch (mode) {
            case 0: coef_list[list_pos] = ccoef + 4; mode_list[list_pos] = 1; /* fallthrough */
            case 2:
                if (mode == 2) { coef_list[list_pos] = 0; mode_list[list_pos++] = 0; }
                for (int i = 0; i < 4; i++, ccoef++) {
                    if (rbit(r)) { coef_list[--list_start] = ccoef; mode_list[list_start] = 3; }
                    else { if (!bits) rbit(r); else { rbits(r, bits); rbit(r); } }
                }
                break;
            case 1:
                mode_list[list_pos] = 2;
                for (int i = 0; i < 3; i++) { ccoef += 4; coef_list[list_end] = ccoef; mode_list[list_end++] = 2; }
                break;
            case 3:
                if (!bits) rbit(r); else { rbits(r, bits); rbit(r); }
                coef_list[list_pos] = 0; mode_list[list_pos++] = 0;
                break;
            }
        }
    }
    rbits(r, 4); // quant
}
static void skip_residue(BR *r, int masks_count) {
    int coef_list[128], mode_list[128], nz = 0;
    int list_start = 64, list_end = 64, list_pos, ccoef, mode;
    coef_list[list_end] = 4; mode_list[list_end++] = 0;
    coef_list[list_end] = 24; mode_list[list_end++] = 0;
    coef_list[list_end] = 44; mode_list[list_end++] = 0;
    coef_list[list_end] = 0; mode_list[list_end++] = 2;
    for (int mask = 1 << rbits(r, 3); mask; mask >>= 1) {
        for (int i = 0; i < nz; i++) { if (!rbit(r)) continue; if (--masks_count < 0) return; }
        list_pos = list_start;
        while (list_pos < list_end) {
            if (!(coef_list[list_pos] | mode_list[list_pos]) || !rbit(r)) { list_pos++; continue; }
            ccoef = coef_list[list_pos]; mode = mode_list[list_pos];
            switch (mode) {
            case 0: coef_list[list_pos] = ccoef + 4; mode_list[list_pos] = 1; /* fallthrough */
            case 2:
                if (mode == 2) { coef_list[list_pos] = 0; mode_list[list_pos++] = 0; }
                for (int i = 0; i < 4; i++, ccoef++) {
                    if (rbit(r)) { coef_list[--list_start] = ccoef; mode_list[list_start] = 3; }
                    else { nz++; rbit(r); if (--masks_count < 0) return; }
                }
                break;
            case 1:
                mode_list[list_pos] = 2;
                for (int i = 0; i < 3; i++) { ccoef += 4; coef_list[list_end] = ccoef; mode_list[list_end++] = 2; }
                break;
            case 3:
                nz++; rbit(r);
                coef_list[list_pos] = 0; mode_list[list_pos++] = 0;
                if (--masks_count < 0) return;
                break;
            }
        }
    }
}

static uint32_t rl32(const uint8_t *p) { return p[0] | p[1] << 8 | p[2] << 16 | (uint32_t)p[3] << 24; }
static void wl32(uint8_t *p, uint32_t v) { p[0] = v; p[1] = v >> 8; p[2] = v >> 16; p[3] = v >> 24; }

/* ======================= remuxing subtitle burner ======================= */
/* Block record parsed from the original stream */
typedef struct {
    int bx, by;          // position (8x8 units)
    int kind;            // 0 = normal 8x8, 1 = scaled 16x16 (at even row/col), 2 = scaled marker (consumes 1 type, covers 2 positions)
    int type, sub;
    int col0, ncol;      // colors bundle range
    int pat0, npat;
    int run0, nrun;
    int xo, yo;          // motion (valid if type 2/4/7)
    int has_x;           // consumes x/y
    int idc, has_idc;
    int ddc, has_ddc;
    long ib0, ib1;       // inline bits [ib0, ib1)
    int replace;         // decision
} Blk;

typedef struct {
    Blk *blk; int nblk; int cap;
    int *rowstart;       // index of first block in each row (size bh+1)
    long tree_start, rows_start, end;
    Ctx ctx;             // trees + bundle data (values)
    int bw, bh;
} PlaneParse;

static void pp_add(PlaneParse *pp, Blk *b) {
    if (pp->nblk == pp->cap) { pp->cap = pp->cap * 2 + 1024; pp->blk = realloc(pp->blk, pp->cap * sizeof(Blk)); }
    pp->blk[pp->nblk++] = *b;
}

static void parse_plane2(BR *r, PlaneParse *pp, int width, int height, int is_chroma) {
    Ctx *c = &pp->ctx;
    int bw = is_chroma ? (width + 15) >> 4 : (width + 7) >> 3;
    int bh = is_chroma ? (height + 15) >> 4 : (height + 7) >> 3;
    int pw = width >> is_chroma;
    pp->bw = bw; pp->bh = bh; pp->nblk = 0;
    pp->tree_start = r->p;
    init_lengths(c, pw > 8 ? pw : 8, bw);
    for (int i = 0; i < NB; i++) {
        if (i == B_COLORS) { for (int k = 0; k < 16; k++) read_tree(r, &c->col_high[k]); c->col_last = 0; }
        if (i != B_IDC && i != B_DDC) read_tree(r, &c->b[i].tree);
        c->b[i].dec = c->b[i].ptr = 0; c->b[i].dead = 0;
    }
    pp->rows_start = r->p;
    for (int by = 0; by < bh; by++) {
        pp->rowstart[by] = pp->nblk;
        rd_types(r, &c->b[B_TYPES]);
        rd_types(r, &c->b[B_SUB]);
        rd_colors(r, &c->b[B_COLORS], c);
        rd_patterns(r, &c->b[B_PATTERN]);
        rd_motion(r, &c->b[B_XOFF]);
        rd_motion(r, &c->b[B_YOFF]);
        rd_dcs(r, &c->b[B_IDC], 11, 0);
        rd_dcs(r, &c->b[B_DDC], 11, 1);
        rd_runs(r, &c->b[B_RUN]);
        for (int bx = 0; bx < bw; bx++) {
            Blk b; memset(&b, 0, sizeof b);
            b.bx = bx; b.by = by;
            b.col0 = c->b[B_COLORS].ptr; b.pat0 = c->b[B_PATTERN].ptr; b.run0 = c->b[B_RUN].ptr;
            int blk = getv(&c->b[B_TYPES]);
            b.type = blk;
            b.ib0 = r->p;
            if (((by & 1) || (bx & 1)) && blk == 1) { b.kind = 2; b.ib1 = r->p; pp_add(pp, &b); bx++; continue; }
            switch (blk) {
            case 0: break;
            case 1: {
                b.kind = 1;
                int sb = getv(&c->b[B_SUB]); b.sub = sb;
                switch (sb) {
                case 3: { rbits(r, 4); int i = 0; do { int run = getv(&c->b[B_RUN]) + 1; i += run; if (rbit(r)) getv(&c->b[B_COLORS]); else for (int j = 0; j < run; j++) getv(&c->b[B_COLORS]); } while (i < 63); if (i == 63) getv(&c->b[B_COLORS]); break; }
                case 5: b.idc = getv(&c->b[B_IDC]); b.has_idc = 1; skip_dct(r); break;
                case 6: getv(&c->b[B_COLORS]); break;
                case 8: getv(&c->b[B_COLORS]); getv(&c->b[B_COLORS]); for (int j = 0; j < 8; j++) getv(&c->b[B_PATTERN]); break;
                case 9: for (int j = 0; j < 64; j++) getv(&c->b[B_COLORS]); break;
                default: fprintf(stderr, "bad sub block %d\n", sb); exit(3);
                }
                break; }
            case 2: b.xo = getv(&c->b[B_XOFF]); b.yo = getv(&c->b[B_YOFF]); b.has_x = 1; break;
            case 3: { rbits(r, 4); int i = 0; do { int run = getv(&c->b[B_RUN]) + 1; i += run; if (rbit(r)) getv(&c->b[B_COLORS]); else for (int j = 0; j < run; j++) getv(&c->b[B_COLORS]); } while (i < 63); if (i == 63) getv(&c->b[B_COLORS]); break; }
            case 4: { b.xo = getv(&c->b[B_XOFF]); b.yo = getv(&c->b[B_YOFF]); b.has_x = 1; int v = rbits(r, 7); skip_residue(r, v); break; }
            case 5: b.idc = getv(&c->b[B_IDC]); b.has_idc = 1; skip_dct(r); break;
            case 6: getv(&c->b[B_COLORS]); break;
            case 7: b.xo = getv(&c->b[B_XOFF]); b.yo = getv(&c->b[B_YOFF]); b.has_x = 1; b.ddc = getv(&c->b[B_DDC]); b.has_ddc = 1; skip_dct(r); break;
            case 8: getv(&c->b[B_COLORS]); getv(&c->b[B_COLORS]); for (int j = 0; j < 8; j++) getv(&c->b[B_PATTERN]); break;
            case 9: c->b[B_COLORS].ptr += 64; break;
            default: fprintf(stderr, "bad block %d\n", blk); exit(3);
            }
            b.ib1 = r->p;
            b.ncol = c->b[B_COLORS].ptr - b.col0; b.npat = c->b[B_PATTERN].ptr - b.pat0; b.nrun = c->b[B_RUN].ptr - b.run0;
            pp_add(pp, &b);
            if (blk == 1) bx++;
        }
    }
    pp->rowstart[bh] = pp->nblk;
    if (r->p & 31) r->p += 32 - (r->p & 31);
    pp->end = r->p;
}

/* ---------------- writing helpers ---------------- */
static void put_dc(BW *w, int v, int nbits, int has_sign) {
    if (has_sign) { int a = v < 0 ? -v : v; wbits(w, a, nbits - 1); if (a) wbit(w, v < 0); }
    else wbits(w, v, nbits);
}
static int bitsneeded(int a) { int n = 0; while (a) { n++; a >>= 1; } return n; }

typedef struct { int *v; int n; int cap; } IVec;
static void iv_push(IVec *a, int x) { if (a->n == a->cap) { a->cap = a->cap * 2 + 256; a->v = realloc(a->v, a->cap * sizeof(int)); } a->v[a->n++] = x; }

/* plane pixel access */
typedef struct { uint8_t *p; int w, h, stride; } Plane;
static int blk_eq(Plane *a, int ax, int ay, Plane *b, int bx, int by, int n) {
    if (ax < 0 || ay < 0 || ax + n > a->w + 8 || ay + n > a->h + 8) return 0;
    for (int y = 0; y < n; y++) {
        int ya = ay + y, yb = by + y;
        if (ya >= a->h || yb >= b->h) continue;            // padding rows: ignore
        for (int x = 0; x < n; x++) {
            int xa = ax + x, xb = bx + x;
            if (xa >= a->w || xb >= b->w) continue;
            if (a->p[ya * a->stride + xa] != b->p[yb * b->stride + xb]) return 0;
        }
    }
    return 1;
}
static int px(Plane *a, int x, int y) { if (x >= a->w) x = a->w - 1; if (y >= a->h) y = a->h - 1; return a->p[y * a->stride + x]; }

/* Emit one plane: original trees, per-row bundles rebuilt from kept blocks + replacements */
typedef struct { int type; int ncols; int cols[64]; int orig; Blk *ob; } OutBlk;

static long STAT_RAW, STAT_FILL, STAT_SKIP, STAT_KEEP;

static void emit_plane(BW *w, const uint8_t *src, PlaneParse *pp, Plane *tgt, Plane *prevstate, Plane *prevorig, Plane *orig) {
    Ctx *c = &pp->ctx;
    int bw = pp->bw, bh = pp->bh;
    // 1) decide replacements per block
    // need per-position map to find scaled blocks covering odd rows
    int nb = pp->nblk;
    for (int i = 0; i < nb; i++) pp->blk[i].replace = 0;
    char *need = calloc(bw * bh + 2 * bw + 2, 1);
    for (int i = 0; i < nb; i++) {
        Blk *b = &pp->blk[i];
        int x = b->bx * 8, y = b->by * 8;
        if (b->kind == 2) continue;
        if (b->kind == 1) {
            if (!blk_eq(tgt, x, y, orig, x, y, 16)) { need[b->by * bw + b->bx] = 1; }
            continue;
        }
        int keep;
        switch (b->type) {
        case 0: keep = blk_eq(prevstate, x, y, tgt, x, y, 8); break;
        case 2: keep = blk_eq(prevstate, x + b->xo, y + b->yo, tgt, x, y, 8); break;
        case 4: case 7: keep = blk_eq(prevstate, x + b->xo, y + b->yo, prevorig, x + b->xo, y + b->yo, 8) && blk_eq(tgt, x, y, orig, x, y, 8); break;
        default: keep = blk_eq(tgt, x, y, orig, x, y, 8); break;
        }
        if (!keep) need[b->by * bw + b->bx] = 1;
    }
    // scaled blocks: replace whole 16x16 if needed (need flag on its anchor)
    // also odd-row markers must follow their anchor
    char *repl = calloc(bw * (bh + 2), 1);   // per 8x8 position: replaced
    for (int i = 0; i < nb; i++) {
        Blk *b = &pp->blk[i];
        if (b->kind == 1) {
            if (need[b->by * bw + b->bx]) { repl[b->by * bw + b->bx] = repl[b->by * bw + b->bx + 1] = repl[(b->by + 1) * bw + b->bx] = repl[(b->by + 1) * bw + b->bx + 1] = 1; }
        } else if (b->kind == 0) {
            if (need[b->by * bw + b->bx]) repl[b->by * bw + b->bx] = 1;
        }
    }
    // a marker whose anchor replaced -> replaced (handled via repl map). A normal 8x8 that needs replace but lies under a marker: impossible
    // 2) build output block lists per row
    OutBlk *ob = malloc(sizeof(OutBlk) * (bw + 2));
    // first pass: collect per-row bundle value lists
    IVec rv[NB]; memset(rv, 0, sizeof rv);
    int **rowcnt = malloc(sizeof(int*) * NB);       // per bundle per row count
    for (int k = 0; k < NB; k++) rowcnt[k] = calloc(bh + 1, sizeof(int));
    int *rowvals_start[NB]; for (int k = 0; k < NB; k++) rowvals_start[k] = calloc(bh + 1, sizeof(int));
    // per-row out blocks stored to replay inline bits later
    OutBlk **rows_ob = malloc(sizeof(OutBlk*) * bh); int *rows_n = calloc(bh, sizeof(int));
    for (int by = 0; by < bh; by++) {
        for (int k = 0; k < NB; k++) rowvals_start[k][by] = rv[k].n;
        int n = 0;
        OutBlk *ro = malloc(sizeof(OutBlk) * (bw + 2));
        for (int i = pp->rowstart[by]; i < pp->rowstart[by + 1]; i++) {
            Blk *b = &pp->blk[i];
            int cover = (b->kind == 0) ? 1 : 2;
            int replaced = repl[b->by * bw + b->bx];
            if (!replaced) {
                // keep original block data
                OutBlk o; o.orig = 1; o.ob = b; o.type = b->type; ro[n++] = o;
                iv_push(&rv[B_TYPES], b->type);
                if (b->kind == 1) iv_push(&rv[B_SUB], b->sub);
                for (int j = 0; j < b->ncol; j++) iv_push(&rv[B_COLORS], c->b[B_COLORS].data[b->col0 + j]);
                for (int j = 0; j < b->npat; j++) iv_push(&rv[B_PATTERN], c->b[B_PATTERN].data[b->pat0 + j]);
                for (int j = 0; j < b->nrun; j++) iv_push(&rv[B_RUN], c->b[B_RUN].data[b->run0 + j]);
                if (b->has_x) { iv_push(&rv[B_XOFF], b->xo); iv_push(&rv[B_YOFF], b->yo); }
                if (b->has_idc) iv_push(&rv[B_IDC], b->idc);
                if (b->has_ddc) iv_push(&rv[B_DDC], b->ddc);
                STAT_KEEP++;
            } else {
                for (int k2 = 0; k2 < cover; k2++) {
                    int bx = b->bx + k2; if (bx >= bw) break;
                    int x = bx * 8, y = by * 8;
                    OutBlk o; o.orig = 0; o.ob = NULL;
                    if (blk_eq(prevstate, x, y, tgt, x, y, 8)) { o.type = 0; o.ncols = 0; STAT_SKIP++; }
                    else {
                        int same = 1, v0 = px(tgt, x, y);
                        for (int yy = 0; yy < 8 && same; yy++) for (int xx = 0; xx < 8; xx++) if (px(tgt, x + xx, y + yy) != v0) { same = 0; break; }
                        if (same) { o.type = 6; o.ncols = 1; o.cols[0] = v0; STAT_FILL++; }
                        else { o.type = 9; o.ncols = 64; for (int k = 0; k < 64; k++) o.cols[k] = px(tgt, x + (k & 7), y + (k >> 3)); STAT_RAW++; }
                    }
                    ro[n++] = o;
                    iv_push(&rv[B_TYPES], o.type);
                    for (int j = 0; j < o.ncols; j++) iv_push(&rv[B_COLORS], o.cols[j]);
                }
            }
        }
        rows_ob[by] = ro; rows_n[by] = n;
        for (int k = 0; k < NB; k++) rowcnt[k][by] = rv[k].n - rowvals_start[k][by];
    }
    for (int k = 0; k < NB; k++) rowvals_start[k][bh] = rv[k].n;
    // 3) write trees (verbatim)
    wcopy(w, src, pp->tree_start, pp->rows_start);
    // 4) per row bundles + inline bits
    int pend[NB] = {0}, dead[NB] = {0}, wpos[NB] = {0};
    int col_last = 0;
    for (int by = 0; by < bh; by++) {
        for (int k = 0; k < NB; k++) {
            Bundle *bd = &c->b[k];
            if (dead[k]) { if (rowcnt[k][by]) { fprintf(stderr, "dead bundle needed\n"); exit(7); } continue; }
            if (pend[k] > 0) { pend[k] -= rowcnt[k][by]; if (pend[k] < 0) { fprintf(stderr, "pending underflow\n"); exit(7); } continue; }
            // ready: decide count
            int cnt = rowcnt[k][by];
            int lastrow = by;
            if (cnt == 0) {
                int r2 = by + 1; while (r2 < bh && rowcnt[k][r2] == 0) r2++;
                if (r2 >= bh) { wbits(w, 0, bd->len); dead[k] = 1; continue; }
                cnt = rowcnt[k][r2]; lastrow = r2;
            }
            int maxc = (1 << bd->len) - 1;
            if (cnt > maxc) { fprintf(stderr, "count overflow bundle %d: %d > %d\n", k, cnt, maxc); exit(7); }
            int s0 = wpos[k];
            wbits(w, cnt, bd->len);
            int *vals = rv[k].v + s0;
            if (k == B_TYPES || k == B_SUB || k == B_RUN) {
                int same = 1; for (int j = 1; j < cnt; j++) if (vals[j] != vals[0]) { same = 0; break; }
                if (same) { wbit(w, 1); wbits(w, vals[0], 4); }
                else { wbit(w, 0); for (int j = 0; j < cnt; j++) huff_write(w, &bd->tree, vals[j]); }
            } else if (k == B_COLORS) {
                wbit(w, 0);
                for (int j = 0; j < cnt; j++) { int hi = vals[j] >> 4, lo = vals[j] & 15; huff_write(w, &c->col_high[col_last], hi); col_last = hi; huff_write(w, &bd->tree, lo); }
            } else if (k == B_PATTERN) {
                for (int j = 0; j < cnt; j++) { huff_write(w, &bd->tree, vals[j] & 15); huff_write(w, &bd->tree, vals[j] >> 4); }
            } else if (k == B_XOFF || k == B_YOFF) {
                wbit(w, 0);
                for (int j = 0; j < cnt; j++) { int a = vals[j] < 0 ? -vals[j] : vals[j]; huff_write(w, &bd->tree, a); if (a) wbit(w, vals[j] < 0); }
            } else { // DCs
                int has_sign = (k == B_DDC);
                put_dc(w, vals[0], 11, has_sign);
                int v = vals[0];
                for (int i = 1; i < cnt; i += 8) {
                    int len2 = cnt - i < 8 ? cnt - i : 8;
                    int mx = 0; for (int j = 0; j < len2; j++) { int d = vals[i + j] - (j ? vals[i + j - 1] : v); if (d < 0) d = -d; if (d > mx) mx = d; }
                    int bsize = bitsneeded(mx);
                    if (bsize > 15) { fprintf(stderr, "dc delta too big\n"); exit(7); }
                    wbits(w, bsize, 4);
                    if (bsize) for (int j = 0; j < len2; j++) { int d = vals[i + j] - (j ? vals[i + j - 1] : v); int a = d < 0 ? -d : d; wbits(w, a, bsize); if (a) wbit(w, d < 0); }
                    v = vals[i + len2 - 1];
                }
            }
            wpos[k] += cnt;
            // consumption this row
            pend[k] = cnt - rowcnt[k][by];
            if (lastrow != by) pend[k] = cnt;  // values belong to a later row; this row consumes none
        }
        // inline bits
        for (int j = 0; j < rows_n[by]; j++) {
            OutBlk *o = &rows_ob[by][j];
            if (o->orig) wcopy(w, src, o->ob->ib0, o->ob->ib1);
        }
        free(rows_ob[by]);
    }
    if (w->p & 31) w->p += 32 - (w->p & 31);
    for (int k = 0; k < NB; k++) { free(rv[k].v); free(rowcnt[k]); free(rowvals_start[k]); }
    free(rowcnt); free(rows_ob); free(rows_n); free(need); free(repl); free(ob);
}

/* ---------------- main ----------------
   sctkosub in.bik out.bik
   stdin: for every frame, the original frame followed by the subtitled frame (raw yuv420p).
   Only the blocks that differ are rewritten; the stream keeps the original Bink revision. */
#ifdef _WIN32
#include <io.h>
#include <fcntl.h>
#endif
static int rd(uint8_t *p, size_t n) { size_t g = 0; while (g < n) { size_t r = fread(p + g, 1, n - g, stdin); if (!r) return 0; g += r; } return 1; }

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: sctkosub in.bik out.bik < frames\n"); return 1; }
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
#endif
    FILE *f = fopen(argv[1], "rb"); if (!f) { fprintf(stderr, "cannot open input\n"); return 2; }
    fseek(f, 0, SEEK_END); long fsz = ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t *buf = malloc(fsz); if (fread(buf, 1, fsz, f) != (size_t)fsz) return 2; fclose(f);
    if (fsz < 48 || buf[0] != 'B' || buf[1] != 'I' || buf[2] != 'K' || buf[3] < 'f' || buf[3] > 'i') { fprintf(stderr, "unsupported Bink revision\n"); return 3; }
    VERSION = buf[3];
    uint32_t nframes = rl32(buf + 8), width = rl32(buf + 20), height = rl32(buf + 24), ntracks = rl32(buf + 40);
    if (rl32(buf + 36) & 0x100000) { fprintf(stderr, "alpha plane not supported\n"); return 3; }
    long hdr = 44 + ntracks * 12;
    uint32_t *idx = malloc(4 * (nframes + 1));
    for (uint32_t i = 0; i <= nframes; i++) idx[i] = rl32(buf + hdr + 4 * i);
    long data0 = hdr + 4 * (nframes + 1);
    int W = width, H = height, CW = W / 2, CH = H / 2;
    long FY = (long)W * H, FC = (long)CW * CH, FS = FY + 2 * FC;
    uint8_t *orig = malloc(FS), *prevorig = calloc(1, FS), *tgt = malloc(FS), *prevstate = calloc(1, FS);
    FILE *of = fopen(argv[2], "wb"); if (!of) { fprintf(stderr, "cannot open output\n"); return 2; }
    uint8_t *ohdr = malloc(data0); memcpy(ohdr, buf, data0);
    fwrite(ohdr, 1, data0, of);
    long first_pos = idx[0] & ~1u;
    if (first_pos > data0) fwrite(buf + data0, 1, first_pos - data0, of);
    long out_pos = first_pos; uint32_t largest = 0;
    uint32_t *nidx = malloc(4 * (nframes + 1));
    int dirty = 0; long changed = 0, extra = 0;
    static PlaneParse pp; pp.rowstart = malloc(sizeof(int) * 1024);
    for (int i = 0; i < NB; i++) pp.ctx.b[i].data = malloc(sizeof(int) * 400000);
    int swap = VERSION >= 'h';
    for (uint32_t fi = 0; fi < nframes; fi++) {
        if (!rd(orig, FS) || !rd(tgt, FS)) { fprintf(stderr, "short input at frame %u\n", fi); return 5; }
        long pos = idx[fi] & ~1u, end = fi + 1 < nframes ? (idx[fi + 1] & ~1u) : fsz;
        nidx[fi] = (uint32_t)out_pos | (idx[fi] & 1);
        int differs = memcmp(tgt, orig, FS) != 0;
        if (!differs && !dirty) {
            fwrite(buf + pos, 1, end - pos, of); out_pos += end - pos;
            if (end - pos > largest) largest = end - pos;
        } else {
            long p = pos;
            for (uint32_t t = 0; t < ntracks; t++) { uint32_t sz = rl32(buf + p); p += 4 + sz; }
            long audio_len = p - pos;
            BR r = { buf + p, end - p, 32 };
            BW w = { calloc(1, 1 << 20), 1 << 20, 0 };
            G_SRCBASE = -1; G_NOPS = 0;
            wbits(&w, 0, 32);
            long plane1_byte = 0;
            for (int pl = 0; pl < 3; pl++) {
                int pidx = (!pl || !swap) ? pl : (pl ^ 3);
                if (pl == 1) plane1_byte = w.p / 8;
                parse_plane2(&r, &pp, W, H, pl > 0);
                long off = pidx == 0 ? 0 : pidx == 1 ? FY : FY + FC;
                int pw = pidx ? CW : W, ph = pidx ? CH : H;
                Plane T = { tgt + off, pw, ph, pw }, PS = { prevstate + off, pw, ph, pw }, PO = { prevorig + off, pw, ph, pw }, O = { orig + off, pw, ph, pw };
                emit_plane(&w, buf + p, &pp, &T, &PS, &PO, &O);
            }
            long vbytes = w.p / 8;
            wl32(w.b, plane1_byte);
            fwrite(buf + pos, 1, audio_len, of);
            fwrite(w.b, 1, vbytes, of);
            long fl = audio_len + vbytes;
            out_pos += fl; if (fl > largest) largest = fl;
            changed++; extra += fl - (end - pos);
            free(w.b);
        }
        dirty = differs;
        memcpy(prevstate, tgt, FS); memcpy(prevorig, orig, FS);
    }
    nidx[nframes] = (uint32_t)out_pos;
    fseek(of, 0, SEEK_SET);
    wl32(ohdr + 4, out_pos - 8);
    wl32(ohdr + 12, largest);
    for (uint32_t i = 0; i <= nframes; i++) wl32(ohdr + hdr + 4 * i, nidx[i]);
    fwrite(ohdr, 1, data0, of);
    if (fclose(of)) { fprintf(stderr, "write error\n"); return 6; }
    fprintf(stderr, "changed %ld of %u frames, +%ld bytes\n", changed, nframes, extra);
    return 0;
}
