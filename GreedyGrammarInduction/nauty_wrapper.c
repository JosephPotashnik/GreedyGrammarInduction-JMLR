// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
///
//to compile (thread-safe with TLS):
//gcc -O3 -DUSE_TLS -shared -fPIC -o nauty_wrapper.dll nauty_wrapper.c nauty.c nautil.c naugraph.c schreier.c naurng.c nausparse.c gtools.c -Wl,--out-implib,libnauty_wrapper.a
// in the directory where nauty source files are located (e.g. C:\nauty\nauty2_9_1rc3). Compiled under windows with mingw-w64. (MSYS2 MINGW64)


#include "nauty.h"
#include "nausparse.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// Export functions for P/Invoke
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT
#endif

// Global pointer to the colors array, used for the qsort comparison function.
// This is a workaround for the non-portable qsort_r function.
// With -DUSE_TLS, all statics become thread-local.
static __thread int* global_colors_ptr;

// Globals for accumulating automorphism generators via callback.
// Thread-local when compiled with -DUSE_TLS.
static __thread int* gen_buffer;
static __thread int gen_count;
static __thread int gen_capacity;

// Structure to return canonical form and mapping
typedef struct {
    int* canonical_labeling;
    int* vertex_mapping;
    int n;
    double group_size;
} CanonicalResult;

// Structure to return automorphism generators
typedef struct {
    int* generators;    // flat array: gen0[0..n-1], gen1[0..n-1], ...
    int gen_count;      // number of generators
    int n;              // number of vertices
    double group_size;  // |Aut(G)|
} AutomorphismResult;

// Structure to return canonical mapping and automorphism orbits without copying
// the full canonical adjacency matrix back to managed code.
typedef struct {
    int* vertex_mapping;
    int* orbits;
    int n;
    double group_size;
} CanonicalOrbitResult;

// Callback for sparsenauty to collect automorphism generators.
static void save_automorphism(int count, int *perm, int *orbits,
                               int numorbits, int stabvertex, int n) {
    if (gen_count < gen_capacity) {
        memcpy(gen_buffer + gen_count * n, perm, n * sizeof(int));
        gen_count++;
    }
}

// Comparison function for qsort to sort vertices by color
// It uses the global_colors_ptr to access the color data.
int compare_colors_qsort(const void* a, const void* b) {
    int v1 = *(const int*)a;
    int v2 = *(const int*)b;
    return global_colors_ptr[v1] - global_colors_ptr[v2];
}

// Function to free result
EXPORT void nauty_free_result(CanonicalResult* result) {
    if (result) {
        free(result->canonical_labeling);
        free(result->vertex_mapping);
        free(result);
    }
}

// Function to free canonical-orbit result
EXPORT void nauty_free_orbit_result(CanonicalOrbitResult* result) {
    if (result) {
        free(result->vertex_mapping);
        free(result->orbits);
        free(result);
    }
}

// Function for sparse graphs with vertex colors
EXPORT CanonicalResult* nauty_canonical_sparse_with_colors(int* starts, int* edges, int n, int totalEdges, int* colors) {
    DYNALLSTAT(int, lab, lab_sz);
    DYNALLSTAT(int, ptn, ptn_sz);
    DYNALLSTAT(int, orbits, orbits_sz);
    DYNALLSTAT(int, map, map_sz);

    // Sparse graph structures
    sparsegraph sg, cg;

    // Statistics and options
    statsblk stats;
    DEFAULTOPTIONS_SPARSEGRAPH(options);

    // Initialize sparse graphs
    SG_INIT(sg);
    SG_INIT(cg);

    // Allocate arrays
    DYNALLOC1(int, lab, lab_sz, n, "malloc");
    DYNALLOC1(int, ptn, ptn_sz, n, "malloc");
    DYNALLOC1(int, orbits, orbits_sz, n, "malloc");
    DYNALLOC1(int, map, map_sz, n, "malloc");

    // Allocate sparse graph arrays using the provided totalEdges count
    SG_ALLOC(sg, n, totalEdges, "malloc");
    SG_ALLOC(cg, n, totalEdges, "malloc");

    // Fill sparse graph with vertex data
    sg.nv = n;
    sg.nde = totalEdges;

    for (int i = 0; i < n; i++) {
        sg.v[i] = starts[i];
        sg.d[i] = (i == n-1) ? totalEdges - starts[i] : starts[i+1] - starts[i];
    }

    // Copy edges
    for (int i = 0; i < totalEdges; i++) {
        sg.e[i] = edges[i];
    }

    // Set up vertex coloring using lab and ptn arrays
    int* colorOrder = (int*)malloc(n * sizeof(int));
    for (int i = 0; i < n; i++) colorOrder[i] = i;
    
    // Set the global pointer for the qsort comparison function.
    global_colors_ptr = colors;

    // Use qsort to sort by color, which is much more efficient than bubble sort
    qsort(colorOrder, n, sizeof(int), compare_colors_qsort);

    // Set up lab and ptn based on colors
    for (int i = 0; i < n; i++) {
        lab[i] = colorOrder[i];
        ptn[i] = (i == n-1 || colors[colorOrder[i]] != colors[colorOrder[i+1]]) ? 0 : 1;
    }

    free(colorOrder);

    // Set options for canonical labeling
    options.getcanon = TRUE;
    options.defaultptn = FALSE; // We're providing our own partitioning

    // Call sparsenauty
    sparsenauty(&sg, lab, ptn, orbits, &options, &stats, &cg);

    // Prepare result
    CanonicalResult* result = (CanonicalResult*)malloc(sizeof(CanonicalResult));
    if (!result) {
        // Clean up and return NULL on failure
        SG_FREE(sg);
        SG_FREE(cg);
        DYNFREE(lab, lab_sz);
        DYNFREE(ptn, ptn_sz);
        DYNFREE(orbits, orbits_sz);
        DYNFREE(map, map_sz);
        return NULL;
    }

    result->canonical_labeling = (int*)malloc(n * n * sizeof(int));
    result->vertex_mapping = (int*)malloc(n * sizeof(int));
    result->n = n;
    result->group_size = stats.grpsize1;

    if (!result->canonical_labeling || !result->vertex_mapping) {
        // Clean up and return NULL on failure
        free(result->canonical_labeling);
        free(result->vertex_mapping);
        free(result);
        SG_FREE(sg);
        SG_FREE(cg);
        DYNFREE(lab, lab_sz);
        DYNFREE(ptn, ptn_sz);
        DYNFREE(orbits, orbits_sz);
        DYNFREE(map, map_sz);
        return NULL;
    }

    // Convert sparse canonical graph back to adjacency matrix
    // Initialize matrix to all zeros
    for (int i = 0; i < n * n; i++) {
        result->canonical_labeling[i] = 0;
    }

    // Fill matrix from sparse representation
    for (int i = 0; i < cg.nv; i++) {
        for (int j = cg.v[i]; j < cg.v[i] + cg.d[i]; j++) {
            int neighbor = cg.e[j];
            if (i >= 0 && i < n && neighbor >= 0 && neighbor < n) {
                result->canonical_labeling[i * n + neighbor] = 1;
            }
        }
    }

    // Copy vertex mapping (labeling)
    for (int i = 0; i < n; i++) {
        result->vertex_mapping[i] = lab[i];
    }

    // Clean up
    SG_FREE(sg);
    SG_FREE(cg);
    DYNFREE(lab, lab_sz);
    DYNFREE(ptn, ptn_sz);
    DYNFREE(orbits, orbits_sz);
    DYNFREE(map, map_sz);

    return result;
}

// Function for sparse graphs with vertex colors. Returns only the canonical
// vertex mapping and nauty's automorphism orbit labels.
EXPORT CanonicalOrbitResult* nauty_canonical_labeling_with_orbits(int* starts, int* edges, int n, int totalEdges, int* colors) {
    DYNALLSTAT(int, lab, lab_sz);
    DYNALLSTAT(int, ptn, ptn_sz);
    DYNALLSTAT(int, orbit_labels, orbit_labels_sz);

    sparsegraph sg, cg;
    statsblk stats;
    DEFAULTOPTIONS_SPARSEGRAPH(options);

    SG_INIT(sg);
    SG_INIT(cg);

    DYNALLOC1(int, lab, lab_sz, n, "malloc");
    DYNALLOC1(int, ptn, ptn_sz, n, "malloc");
    DYNALLOC1(int, orbit_labels, orbit_labels_sz, n, "malloc");

    SG_ALLOC(sg, n, totalEdges, "malloc");
    SG_ALLOC(cg, n, totalEdges, "malloc");

    sg.nv = n;
    sg.nde = totalEdges;

    for (int i = 0; i < n; i++) {
        sg.v[i] = starts[i];
        sg.d[i] = (i == n-1) ? totalEdges - starts[i] : starts[i+1] - starts[i];
    }

    for (int i = 0; i < totalEdges; i++) {
        sg.e[i] = edges[i];
    }

    int* colorOrder = (int*)malloc(n * sizeof(int));
    if (!colorOrder) {
        SG_FREE(sg);
        SG_FREE(cg);
        DYNFREE(lab, lab_sz);
        DYNFREE(ptn, ptn_sz);
        DYNFREE(orbit_labels, orbit_labels_sz);
        return NULL;
    }

    for (int i = 0; i < n; i++) colorOrder[i] = i;

    global_colors_ptr = colors;
    qsort(colorOrder, n, sizeof(int), compare_colors_qsort);

    for (int i = 0; i < n; i++) {
        lab[i] = colorOrder[i];
        ptn[i] = (i == n-1 || colors[colorOrder[i]] != colors[colorOrder[i+1]]) ? 0 : 1;
    }

    free(colorOrder);

    options.getcanon = TRUE;
    options.defaultptn = FALSE;

    sparsenauty(&sg, lab, ptn, orbit_labels, &options, &stats, &cg);

    CanonicalOrbitResult* result = (CanonicalOrbitResult*)malloc(sizeof(CanonicalOrbitResult));
    if (!result) {
        SG_FREE(sg);
        SG_FREE(cg);
        DYNFREE(lab, lab_sz);
        DYNFREE(ptn, ptn_sz);
        DYNFREE(orbit_labels, orbit_labels_sz);
        return NULL;
    }

    result->vertex_mapping = (int*)malloc(n * sizeof(int));
    result->orbits = (int*)malloc(n * sizeof(int));
    result->n = n;
    result->group_size = stats.grpsize1;

    if (!result->vertex_mapping || !result->orbits) {
        free(result->vertex_mapping);
        free(result->orbits);
        free(result);
        SG_FREE(sg);
        SG_FREE(cg);
        DYNFREE(lab, lab_sz);
        DYNFREE(ptn, ptn_sz);
        DYNFREE(orbit_labels, orbit_labels_sz);
        return NULL;
    }

    for (int i = 0; i < n; i++) {
        result->vertex_mapping[i] = lab[i];
        result->orbits[i] = orbit_labels[i];
    }

    SG_FREE(sg);
    SG_FREE(cg);
    DYNFREE(lab, lab_sz);
    DYNFREE(ptn, ptn_sz);
    DYNFREE(orbit_labels, orbit_labels_sz);

    return result;
}

// Function to free automorphism result
EXPORT void nauty_free_automorphism_result(AutomorphismResult* result) {
    if (result) {
        free(result->generators);
        free(result);
    }
}

// Compute automorphism generators for a colored sparse graph.
// Does NOT compute canonical form (faster than the full canonical function).
EXPORT AutomorphismResult* nauty_get_automorphism_generators(int* starts, int* edges, int n, int totalEdges, int* colors) {
    int* lab = (int*)malloc(n * sizeof(int));
    int* ptn = (int*)malloc(n * sizeof(int));
    int* orbits_arr = (int*)malloc(n * sizeof(int));

    if (!lab || !ptn || !orbits_arr) {
        free(lab); free(ptn); free(orbits_arr);
        return NULL;
    }

    sparsegraph sg, cg;
    statsblk stats;
    DEFAULTOPTIONS_SPARSEGRAPH(options);

    SG_INIT(sg);
    SG_INIT(cg);

    SG_ALLOC(sg, n, totalEdges, "malloc");
    SG_ALLOC(cg, n, totalEdges, "malloc");

    sg.nv = n;
    sg.nde = totalEdges;

    for (int i = 0; i < n; i++) {
        sg.v[i] = starts[i];
        sg.d[i] = (i == n-1) ? totalEdges - starts[i] : starts[i+1] - starts[i];
    }

    for (int i = 0; i < totalEdges; i++) {
        sg.e[i] = edges[i];
    }

    // Set up vertex coloring
    int* colorOrder = (int*)malloc(n * sizeof(int));
    if (!colorOrder) {
        SG_FREE(sg);
        SG_FREE(cg);
        free(lab); free(ptn); free(orbits_arr);
        return NULL;
    }
    for (int i = 0; i < n; i++) colorOrder[i] = i;

    global_colors_ptr = colors;
    qsort(colorOrder, n, sizeof(int), compare_colors_qsort);

    for (int i = 0; i < n; i++) {
        lab[i] = colorOrder[i];
        ptn[i] = (i == n-1 || colors[colorOrder[i]] != colors[colorOrder[i+1]]) ? 0 : 1;
    }

    free(colorOrder);

    // Set up generator buffer. Truncation is conservative for our C# filters: fewer
    // generators means less pruning, never extra pruning.
    gen_capacity = (n > 0) ? n : 1;
    gen_count = 0;
    gen_buffer = (int*)malloc(gen_capacity * n * sizeof(int));
    if (!gen_buffer) {
        SG_FREE(sg);
        SG_FREE(cg);
        free(lab); free(ptn); free(orbits_arr);
        return NULL;
    }

    options.getcanon = FALSE;
    options.defaultptn = FALSE;
    options.userautomproc = save_automorphism;

    sparsenauty(&sg, lab, ptn, orbits_arr, &options, &stats, &cg);

    AutomorphismResult* result = (AutomorphismResult*)malloc(sizeof(AutomorphismResult));
    if (!result) {
        free(gen_buffer);
        gen_buffer = NULL;
        SG_FREE(sg);
        SG_FREE(cg);
        free(lab); free(ptn); free(orbits_arr);
        return NULL;
    }

    if (gen_count > 0) {
        result->generators = (int*)malloc(gen_count * n * sizeof(int));
        if (result->generators) {
            memcpy(result->generators, gen_buffer, gen_count * n * sizeof(int));
        } else {
            gen_count = 0;
        }
    } else {
        result->generators = NULL;
    }
    result->gen_count = gen_count;
    result->n = n;
    result->group_size = stats.grpsize1;

    free(gen_buffer);
    gen_buffer = NULL;

    SG_FREE(sg);
    SG_FREE(cg);
    free(lab); free(ptn); free(orbits_arr);

    return result;
}
