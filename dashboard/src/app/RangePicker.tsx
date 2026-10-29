import { Icon } from '../components/icons'
import { Segmented } from '../components/ui'
import { RANGE_PRESETS, type Range, type RangePreset } from './route'

export function RangePicker({ range, onChange }: { range: Range; onChange: (patch: Record<string, string | undefined>) => void }) {
  return (
    <div className="range">
      {range.preset === 'custom' && (
        <div className="date-inputs">
          <input type="date" value={range.from ?? ''} onChange={(e) => onChange({ from: e.target.value })} aria-label="Start date" />
          <span className="muted">→</span>
          <input type="date" value={range.to ?? ''} onChange={(e) => onChange({ to: e.target.value })} aria-label="End date" />
        </div>
      )}
      <Icon name="calendar" className="icon-svg range-icon" />
      <Segmented<RangePreset> label="Date range" value={range.preset} options={RANGE_PRESETS}
        onChange={(id) => onChange({ range: id, ...(id === 'custom' ? {} : { from: undefined, to: undefined }) })} />
    </div>
  )
}
