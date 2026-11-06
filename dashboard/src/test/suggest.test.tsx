import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import type { Metadata } from '../api/types'
import { MetaContext, SuggestInput, type SuggestList } from '../components/editors'

afterEach(cleanup)

const meta = {
  events: [{ name: '$pageview' }, { name: 'add_to_cart' }, { name: 'purchase' }, { name: 'signed_up' }],
  event_columns: ['$browser'],
  event_properties: [{ name: '$browser', type: 'string' }, { name: 'amount', type: 'number' }],
  person_properties: [{ name: 'plan', type: 'string' }],
} as unknown as Metadata

function Harness({ list = 'events', initial = '$pageview' }: { list?: SuggestList; initial?: string }) {
  const [value, setValue] = useState(initial)
  return (
    <MetaContext.Provider value={meta}>
      <SuggestInput list={list} value={value} onChange={setValue} aria-label="Series 1 event" />
      <output data-testid="value">{value}</output>
    </MetaContext.Provider>
  )
}

const options = () => screen.queryAllByRole('option').map((o) => o.textContent)
const input = () => screen.getByRole('combobox', { name: 'Series 1 event' })

describe('SuggestInput', () => {
  it('shows every option on focus even when the box already has a value', () => {
    render(<Harness />)
    expect(options()).toEqual([])
    fireEvent.focus(input())
    expect(options()).toEqual(['$pageview', 'add_to_cart', 'purchase', 'signed_up'])
    expect(screen.getByRole('option', { name: '$pageview' }).getAttribute('aria-selected')).toBe('true')
  })

  it('opens the full list from the arrow button', () => {
    render(<Harness />)
    fireEvent.click(screen.getByRole('button', { name: 'Show options' }))
    expect(options()).toHaveLength(4)
    fireEvent.click(screen.getByRole('button', { name: 'Show options' }))
    expect(options()).toEqual([])
  })

  it('filters while typing and picks an option with the mouse', () => {
    render(<Harness />)
    fireEvent.focus(input())
    fireEvent.change(input(), { target: { value: 'PUR' } })
    expect(options()).toEqual(['purchase'])
    fireEvent.mouseDown(screen.getByRole('option', { name: 'purchase' }))
    expect(screen.getByTestId('value').textContent).toBe('purchase')
    expect(options()).toEqual([])
  })

  it('supports the keyboard: arrows move, enter picks, escape closes', () => {
    render(<Harness />)
    fireEvent.focus(input())
    fireEvent.keyDown(input(), { key: 'ArrowDown' })
    fireEvent.keyDown(input(), { key: 'ArrowDown' })
    expect(input().getAttribute('aria-activedescendant')).toBe(screen.getByRole('option', { name: 'add_to_cart' }).id)
    fireEvent.keyDown(input(), { key: 'Enter' })
    expect(screen.getByTestId('value').textContent).toBe('add_to_cart')
    fireEvent.keyDown(input(), { key: 'ArrowDown' })
    expect(options()).toHaveLength(4)
    fireEvent.keyDown(input(), { key: 'Escape' })
    expect(options()).toEqual([])
  })

  it('keeps free text that is not in the list', () => {
    render(<Harness />)
    fireEvent.focus(input())
    fireEvent.change(input(), { target: { value: 'custom_event' } })
    expect(options()).toEqual([])
    expect(screen.getByTestId('value').textContent).toBe('custom_event')
  })

  it('lists event columns and properties once, with their types', () => {
    render(<Harness list="event-props" initial="" />)
    fireEvent.focus(input())
    expect(options()).toEqual(['$browser', 'amountnumber'])
  })
})
