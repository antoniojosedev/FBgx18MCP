//! Chunked scoped-thread execution shared by the rebuild phases.

/// Splits `items` into at most `concurrency` contiguous chunks and runs `work` on each in its own scoped
/// thread. Each chunk returns its own result and the results come back in chunk order, so the caller's merge
/// is deterministic and no state is shared between threads.
pub(super) fn map_chunks<T: Sync, R: Send>(
    items: &[T],
    concurrency: usize,
    work: impl Fn(&[T]) -> R + Sync,
) -> Vec<R> {
    if items.is_empty() {
        return Vec::new();
    }
    let thread_count = concurrency.min(items.len()).max(1);
    let chunk_size = items.len().div_ceil(thread_count);
    let work = &work;
    std::thread::scope(|scope| {
        let handles: Vec<_> = items
            .chunks(chunk_size)
            .map(|chunk| scope.spawn(move || work(chunk)))
            .collect();
        handles
            .into_iter()
            .map(|handle| {
                handle
                    .join()
                    .unwrap_or_else(|panic| std::panic::resume_unwind(panic))
            })
            .collect()
    })
}

/// Errors and warnings a phase records, merged by the caller after its threads finish.
#[derive(Debug, Default)]
pub(super) struct Findings {
    pub errors: Vec<String>,
    pub warnings: Vec<String>,
}

impl Findings {
    pub fn merge(&mut self, other: Findings) {
        self.errors.extend(other.errors);
        self.warnings.extend(other.warnings);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn results_come_back_in_chunk_order_for_any_concurrency() {
        let items: Vec<u32> = (0..10).collect();
        for concurrency in [0, 1, 3, 10, 64] {
            let merged: Vec<u32> = map_chunks(&items, concurrency, |chunk| chunk.to_vec())
                .into_iter()
                .flatten()
                .collect();
            assert_eq!(merged, items, "concurrency {concurrency}");
        }
    }

    #[test]
    fn no_items_spawn_no_work() {
        let items: Vec<u32> = Vec::new();
        assert!(map_chunks(&items, 4, |chunk| chunk.len()).is_empty());
    }

    #[test]
    fn chunks_never_exceed_the_requested_concurrency() {
        let items: Vec<u32> = (0..10).collect();
        assert_eq!(map_chunks(&items, 3, |chunk| chunk.len()).len(), 3);
    }
}
