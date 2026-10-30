import type { RfmSegmentKey } from '../../api/types'

export const RFM_WINDOWS = [90, 180, 365]

export const RFM_SEGMENT_NAMES: Record<RfmSegmentKey, string> = {
  champions: 'Champions',
  loyal: 'Loyal',
  potential_loyalist: 'Potential Loyalist',
  new_customers: 'New Customers',
  promising: 'Promising',
  need_attention: 'Need Attention',
  about_to_sleep: 'About to Sleep',
  at_risk: 'At Risk',
  cant_lose: "Can't Lose",
  hibernating: 'Hibernating',
  lost: 'Lost',
}

export const RFM_SEGMENT_KEYS = Object.keys(RFM_SEGMENT_NAMES) as RfmSegmentKey[]
