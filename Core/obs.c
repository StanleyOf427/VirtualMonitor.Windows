#include <inttypes.h>

#include "graphics/matrix4.h"
#include "callback/calldata.h"
//
#include "obs.h"
#include "obs-internal.h"
#include "media-io/video-io.h"

struct obs_core* obs = NULL;

extern void add_default_module_paths(void);
extern char* find_libobs_data_file(const char* file);

void start_raw_video(video_t* v, const struct video_scale_info* conversion,
	void (*callback)(void* param, struct video_data* frame),
	void* param)
{
	struct obs_core_video* video = &obs->video;
	os_atomic_inc_long(&video->raw_active);
	video_output_connect(v, conversion, callback, param);
}

static int obs_init_video(struct obs_video_info *ovi)
{
	struct obs_core_video *video = &obs->video;
	struct video_output_info vi;
	pthread_mutexattr_t attr;
	int errorcode;

	make_video_info(&vi, ovi);
	video->base_width = ovi->base_width;
	video->base_height = ovi->base_height;
	video->output_width = ovi->output_width;
	video->output_height = ovi->output_height;
	video->gpu_conversion = ovi->gpu_conversion;
	video->scale_type = ovi->scale_type;

	set_video_matrix(video, ovi);

	errorcode = video_output_open(&video->video, &vi);

	if (errorcode != VIDEO_OUTPUT_SUCCESS) {
		if (errorcode == VIDEO_OUTPUT_INVALIDPARAM) {
			blog(LOG_ERROR, "Invalid video parameters specified");
			return OBS_VIDEO_INVALID_PARAM;
		}
		else {
			blog(LOG_ERROR, "Could not open video output");
		}
		return OBS_VIDEO_FAIL;
	}

	gs_enter_context(video->graphics);

	if (ovi->gpu_conversion && !obs_init_gpu_conversion(ovi))
		return OBS_VIDEO_FAIL;
	if (!obs_init_textures(ovi))
		return OBS_VIDEO_FAIL;

	gs_leave_context();

	if (pthread_mutexattr_init(&attr) != 0)
		return OBS_VIDEO_FAIL;
	if (pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE) != 0)
		return OBS_VIDEO_FAIL;
	if (pthread_mutex_init(&video->gpu_encoder_mutex, NULL) < 0)
		return OBS_VIDEO_FAIL;
	if (pthread_mutex_init(&video->task_mutex, NULL) < 0)
		return OBS_VIDEO_FAIL;

	errorcode = pthread_create(&video->video_thread, NULL,
		obs_graphics_thread, obs);
	if (errorcode != 0)
		return OBS_VIDEO_FAIL;

	video->thread_initialized = true;
	video->ovi = *ovi;
	return OBS_VIDEO_SUCCESS;
}

static int new_initvideo(struct obs_video_info  *ovi)
{
	struct obs_core_video *video = &obs->video;
	struct video_output_info vi;
	pthread_mutexattr_t attr;
	int errorcode;

	make_video_info(&vi, ovi);
	video->base_width = ovi->base_width;
	video->base_height = ovi->base_height;
	video->output_width = ovi->output_width;
	video->output_height = ovi->output_height;
	video->gpu_conversion = ovi->gpu_conversion;
	video->scale_type = ovi->scale_type;
	set_video_matrix(video, ovi);

	errorcode = video_output_open(&video->video, &vi);


	if (errorcode != VIDEO_OUTPUT_SUCCESS) {
		if (errorcode == VIDEO_OUTPUT_INVALIDPARAM) {
			blog(LOG_ERROR, "Invalid video parameters specified");
			return OBS_VIDEO_INVALID_PARAM;
		}
		else {
			blog(LOG_ERROR, "Could not open video output");
		}
		return OBS_VIDEO_FAIL;
	}

	gs_enter_context(video->graphics);

	if (ovi->gpu_conversion && !obs_init_gpu_conversion(ovi))
		return OBS_VIDEO_FAIL;
	if (!obs_init_textures(ovi))
		return OBS_VIDEO_FAIL;

	gs_leave_context();


	if (pthread_mutexattr_init(&attr) != 0)
		return OBS_VIDEO_FAIL;
	if (pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE) != 0)
		return OBS_VIDEO_FAIL;
	if (pthread_mutex_init(&video->gpu_encoder_mutex, NULL) < 0)
		return OBS_VIDEO_FAIL;
	if (pthread_mutex_init(&video->task_mutex, NULL) < 0)
		return OBS_VIDEO_FAIL;

	errorcode = pthread_create(&video->video_thread, NULL,
		obs_graphics_thread, obs);
	if (errorcode != 0)
		return OBS_VIDEO_FAIL;

	video->thread_initialized = true;
	video->ovi = *ovi;
	return OBS_VIDEO_SUCCESS;
}

int obs_reset_video(struct obs_video_info *ovi)
{
	if (!obs)
		return OBS_VIDEO_FAIL;

	/* don't allow changing of video settings if active. */
	if (obs->video.video && obs_video_active())
		return OBS_VIDEO_CURRENTLY_ACTIVE;

	if (!size_valid(ovi->output_width, ovi->output_height) ||
		!size_valid(ovi->base_width, ovi->base_height))
		return OBS_VIDEO_INVALID_PARAM;

	struct obs_core_video* video = &obs->video;

	stop_video();
	obs_free_video();

	/* align to multiple-of-two and SSE alignment sizes */
	ovi->output_width &= 0xFFFFFFFC;
	ovi->output_height &= 0xFFFFFFFE;

	if (!video->graphics) {
		int errorcode = obs_init_graphics(ovi);
		if (errorcode != OBS_VIDEO_SUCCESS) {
			obs_free_graphics();
			return errorcode;
		}
	}

	const char* scale_type_name = "";
	switch (ovi->scale_type) {
	case OBS_SCALE_DISABLE:
		scale_type_name = "Disabled";
		break;
	case OBS_SCALE_POINT:
		scale_type_name = "Point";
		break;
	case OBS_SCALE_BICUBIC:
		scale_type_name = "Bicubic";
		break;
	case OBS_SCALE_BILINEAR:
		scale_type_name = "Bilinear";
		break;
	case OBS_SCALE_LANCZOS:
		scale_type_name = "Lanczos";
		break;
	case OBS_SCALE_AREA:
		scale_type_name = "Area";
		break;
	}

	bool yuv = format_is_yuv(ovi->output_format);
	const char* yuv_format = get_video_colorspace_name(ovi->colorspace);
	const char* yuv_range =
		get_video_range_name(ovi->output_format, ovi->range);

	blog(LOG_INFO, "---------------------------------");
	blog(LOG_INFO,
		"video settings reset:\n"
		"\tbase resolution:   %dx%d\n"
		"\toutput resolution: %dx%d\n"
		"\tdownscale filter:  %s\n"
		"\tfps:               %d/%d\n"
		"\tformat:            %s\n"
		"\tYUV mode:          %s%s%s",
		ovi->base_width, ovi->base_height, ovi->output_width,
		ovi->output_height, scale_type_name, ovi->fps_num, ovi->fps_den,
		get_video_format_name(ovi->output_format),
		yuv ? yuv_format : "None", yuv ? "/" : "", yuv ? yuv_range : "");

	return obs_init_video(ovi);
}
