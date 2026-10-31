import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Segmented, Status } from '../components/ui'

afterEach(cleanup)

describe('Segmented', () => {
  it('marks the current option and reports a new choice', () => {
    const onChange = vi.fn()
    render(<Segmented label="Interval" value="day" options={[{ id: 'day', label: 'Day' }, { id: 'week', label: 'Week' }]} onChange={onChange} />)
    expect(screen.getByRole('button', { name: 'Day' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: 'Week' }).getAttribute('aria-pressed')).toBe('false')
    fireEvent.click(screen.getByRole('button', { name: 'Week' }))
    expect(onChange).toHaveBeenCalledWith('week')
  })
})

describe('Status', () => {
  it('shows a loading skeleton before the first result', () => {
    render(<Status state={{ loading: true }}><p>chart</p></Status>)
    expect(screen.getByLabelText('Loading')).toBeTruthy()
    expect(screen.queryByText('chart')).toBeNull()
  })

  it('shows the error from the API', () => {
    render(<Status state={{ loading: false, error: 'Query failed in the analytics database' }}><p>chart</p></Status>)
    expect(screen.getByRole('alert').textContent).toContain('Query failed in the analytics database')
  })

  it('keeps the previous result visible while reloading and explains an empty result', () => {
    const { container, rerender } = render(<Status state={{ loading: true, data: [1] }}><p>chart</p></Status>)
    expect(screen.getByText('chart')).toBeTruthy()
    expect(container.querySelector('.status-body.stale')).not.toBeNull()
    rerender(<Status state={{ loading: false, data: [] }} empty><p>chart</p></Status>)
    expect(screen.getByText('No data in this range')).toBeTruthy()
  })
})
