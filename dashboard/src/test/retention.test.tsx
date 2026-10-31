import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { RetentionResponse } from '../api/types'
import { RetentionTable } from '../features/retention/Retention'

afterEach(cleanup)

const data: RetentionResponse = {
  period: 'day', periods: 3, timezone: 'Europe/Istanbul', took_ms: 12,
  cohorts: [
    { cohort_start: '2026-10-29T00:00:00Z', size: 10, counts: [10, 5, 2] },
    { cohort_start: '2026-10-30T00:00:00Z', size: 4, counts: [4, 1, null] },
  ],
  average: [1, 0.4, 0.2],
}

describe('RetentionTable', () => {
  it('shows one row per cohort with percentages and leaves future periods empty', () => {
    render(<RetentionTable data={data} />)
    const table = screen.getByTestId('retention-table')
    const rows = within(table).getAllByRole('row')
    expect(rows).toHaveLength(4)
    expect(rows[2].textContent).toContain('50%')
    expect(rows[3].querySelectorAll('td.heat-none')).toHaveLength(1)
    expect(within(rows[1]).getByText('14')).toBeTruthy()
  })

  it('limits the number of cohorts when asked', () => {
    render(<RetentionTable data={data} maxCohorts={1} />)
    expect(within(screen.getByTestId('retention-table')).getAllByRole('row')).toHaveLength(3)
  })
})
