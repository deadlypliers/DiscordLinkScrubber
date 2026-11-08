# Tracking parameters for the built-in sites added in revision 2

> Revision 3 split each shipped list into **identifying** params (`StripParams`, which trigger a repost) and
> **cosmetic** ones (`TidyParams`, removed only alongside an identifying hit). The table below lists everything
> shipped, regardless of tier; see `DefaultSites.cs` for the split. `utm_*` is no longer in any site list: the
> server-wide rule treats it by value (generic labels are noise, other values identify), see `GlobalRule.cs`.

Checked on 2026-11-07 against the ClearURLs rules database (<https://rules2.clearurls.xyz/data.minify.json>,
repo licence LGPL-3.0). It is used as a **reference only**: the lists in `DefaultSites.cs` were written by hand
and contain only parameters it confirms, minus any that look functional.

| Site | Shipped | Deliberately kept (functional) / not shipped |
| --- | --- | --- |
| amazon | `tag ascsubtag pf_rd_* pd_rd_* qid sr srs ref ref_ linkCode creativeASIN camp creative content-id crid sprefix _encoding dib dib_tag social_share`, path `/ref=…` | `th`, `psc` (variant choice), `smid` (seller), `keywords` (search text) |
| ebay | `_trkparms _trksid _from` | `hash` (item variant) |
| aliexpress | `ws_ab_test btsid algo_expid algo_pvid gps-id scm* sk terminal_id aff_request_id mall_affr` | `cv`, `af`, `dp` (unclear) |
| etsy | `click_key click_sum organic_search_click` | |
| linkedin | `trk trackingId refId lipi licu` | |
| apple | `itscg itsct ign-itsc*` (itscg/itsct are Apple's marketing tokens) | `i` (track id), `app` |
| netflix | `trackId tctx` | `jbv` (title id; ClearURLs' `jb*` would break links) |
| steam | `snr` (steampowered.com only) | |
| google | `ved ei oq uact aqs sourceid sxsrf rlz sca_esv sca_upv iflsig gs_* gws_* pcampaignid` | `q`, `source`, `site`, `ie`, `sa` (can change results) |
| bing | `cvid sp sc qs qp form` | `q` |
| medium | `source` | `sk` (friend-link access key) |

Not added because ClearURLs has no entry and no other source was checked: Threads, Pinterest, SoundCloud,
Bandcamp, Substack, Snapchat, Bluesky, Tumblr, Vimeo, Telegram. Admins can add them with `/linkcleaner site add`
and `/linkcleaner param add`; `utm_*` is already removed from them by the server-wide rule.
