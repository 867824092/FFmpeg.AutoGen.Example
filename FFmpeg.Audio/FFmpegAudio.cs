using FFmpeg.AutoGen;
using FFmpeg.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpeg.Audio {
    public class FFmpegAudio {
        static int OUTPUT_CHANNELS = 2;//双通道
        static int OUTPUT_BIT_RATE = 128000;//比特率
        static int pts = 0;//时间戳
        public static unsafe void Run(string inputUrl,string outUrl, CancellationToken token) {
#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
            Console.WriteLine();
#endif
            //输入
            AVFormatContext* input_format_context = null;
            AVCodecContext* input_codec_context = null;
            //输出
            AVFormatContext* output_format_context = null;
            AVCodecContext* output_codec_context = null;
            //重采样
            SwrContext* swr_context = null;
            AVAudioFifo* fifo = null;
            int streamIndex = -1;
            AVInputFormat* input_format = ffmpeg.av_find_input_format("dshow");
            if (Open_Input_File(&input_format_context, &input_codec_context, inputUrl, input_format, ref streamIndex) < 0) {
                goto cleanup;
            }
            if (Open_Output_File(&output_format_context, &output_codec_context, &input_codec_context, outUrl) < 0) {
                goto cleanup;
            }
            if (Init_SwrContext(input_codec_context, output_codec_context, &swr_context) < 0) {
                goto cleanup;
            }
            if (Init_fifo(&fifo, output_codec_context) != 0) {
                goto cleanup;
            }
            //写入文件头
            ffmpeg.avformat_write_header(output_format_context, null);
            int error = -1;
            while (!token.IsCancellationRequested) {
                int output_frame_size = output_codec_context->frame_size;
                while (ffmpeg.av_audio_fifo_size(fifo) < output_frame_size) {
                    if ((error = read_decode_convert_and_store(fifo, input_format_context, input_codec_context, output_codec_context,
                    swr_context)) < 0) {
                        Console.WriteLine($"read_decode_convert_and_store error:{FFmpegHelper.av_err2str(error)}");
                        goto cleanup;
                    }
                }
                while (ffmpeg.av_audio_fifo_size(fifo) >= output_frame_size
                    || ffmpeg.av_audio_fifo_size(fifo) > 0) {
                    if (load_encode_and_write(fifo, output_format_context, output_codec_context) < 0) {
                        goto cleanup;
                    }
                }
            }
            ffmpeg.av_write_trailer(output_format_context);

        cleanup:
            ffmpeg.avformat_close_input(&input_format_context);
            ffmpeg.avcodec_free_context(&input_codec_context);
            ffmpeg.swr_free(&swr_context);
            ffmpeg.avcodec_free_context(&output_codec_context);
            ffmpeg.avio_closep(&(output_format_context->pb));
            ffmpeg.avformat_free_context(output_format_context);
        }
        static unsafe int Open_Input_File(AVFormatContext** input_format_context,
        AVCodecContext** input_codec_context, string file, AVInputFormat* input_formant,ref int streamIndex) {
            int error = 0;
            //打开文件
            error = ffmpeg.avformat_open_input(input_format_context, file, input_formant, null);
            if (error < 0) {
                Console.WriteLine($"Could not open input file '{file}' (error '{FFmpegHelper.av_err2str(error)}')");
                return error;
            }
            //查找流
            error = ffmpeg.avformat_find_stream_info(*input_format_context, null);
            if (error < 0) {
                Console.WriteLine($"Could not find stream info (error '{FFmpegHelper.av_err2str(error)}')");
                return error;
            }
            //查找音频流位置
            for (int i = 0; i < (*input_format_context)->nb_streams; i++) {
                if ((*input_format_context)->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) {
                    streamIndex = i;
                    break;
                }
            }
            //查找解码信息
            AVCodec* decodec = ffmpeg.avcodec_find_decoder((*input_format_context)->streams[streamIndex]->codecpar->codec_id);
            if (decodec == null) {
                Console.WriteLine($"Could not find decoder {(*input_format_context)->streams[streamIndex]->codecpar->codec_id}");
                return ffmpeg.AVERROR_EXIT;
            }
            AVCodecContext* avcCtx = ffmpeg.avcodec_alloc_context3(decodec);
            if (avcCtx == null) {
                Console.WriteLine("Could not allocate a decoding context");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }
            error = ffmpeg.avcodec_parameters_to_context(avcCtx, (*input_format_context)->streams[streamIndex]->codecpar);
            if (error < 0) {
                ffmpeg.avformat_close_input(input_format_context);
                ffmpeg.avcodec_free_context(&avcCtx);
                return error;
            }
            //打开解码器
            error = ffmpeg.avcodec_open2(avcCtx, decodec, null);
            if (error < 0) {
                Console.WriteLine($"Could not open input codec (error '{FFmpegHelper.av_err2str(error)}')");
                ffmpeg.avcodec_free_context(&avcCtx);
                ffmpeg.avformat_close_input(input_format_context);
                return error;
            }
            *input_codec_context = avcCtx;
            return error;
        }

        static unsafe int Open_Output_File(AVFormatContext** output_format_context, AVCodecContext** output_codec_context, AVCodecContext** input_codec_context,
        string file) {
            int error = 0;
            error = ffmpeg.avformat_alloc_output_context2(output_format_context, null, null, file);
            if (error < 0) {
                Console.WriteLine("Could not allocate output format context");
                return error;
            }
            //打开文件
            error = ffmpeg.avio_open(&(*output_format_context)->pb, file, ffmpeg.AVIO_FLAG_WRITE);
            if (error < 0) {
                Console.WriteLine($"Could not open output file '{file}' (error '{FFmpegHelper.av_err2str(error)}')");
                return error;
            }
            //查找编码器
            AVCodec* encodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (encodec == null) {
                Console.WriteLine($"Could not find encoder AV_CODEC_ID_AAC");
                return ffmpeg.AVERROR_EXIT;
            }
            AVCodecContext* avcCtx = ffmpeg.avcodec_alloc_context3(encodec);
            if (avcCtx == null) {
                Console.WriteLine("Could not allocate an encoding context");
                error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto cleanup;
            }
            avcCtx->channels = OUTPUT_CHANNELS;
            avcCtx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(OUTPUT_CHANNELS);
            //采样率 一般为44.1KHZ
            avcCtx->sample_rate = (*input_codec_context)->sample_rate;
            avcCtx->sample_fmt = encodec->sample_fmts[0];
            avcCtx->bit_rate = OUTPUT_BIT_RATE;

            if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER) {
                avcCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }
            //打开编码器
            error = ffmpeg.avcodec_open2(avcCtx, encodec, null);
            if (error < 0) {
                Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            //创建输出流
            AVStream* stream = ffmpeg.avformat_new_stream(*output_format_context, encodec);
            if (stream == null) {
                Console.WriteLine("Could not create new stream");
                error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto cleanup;
            }
            stream->time_base.den = (*input_codec_context)->sample_rate;
            stream->time_base.num = 1;
            error = ffmpeg.avcodec_parameters_from_context(stream->codecpar, avcCtx);
            if (error < 0) {
                Console.WriteLine("Could not initialize stream parameters");
                goto cleanup;
            }
            *output_codec_context = avcCtx;
            return 0;
        cleanup:
            ffmpeg.avcodec_free_context(&avcCtx);
            ffmpeg.avio_closep(&(*output_format_context)->pb);
            ffmpeg.avformat_free_context(*output_format_context);
            *output_format_context = null;
            return ffmpeg.AVERROR_EXIT;
        }

        static unsafe int Init_SwrContext(AVCodecContext* input_codec_context, AVCodecContext* output_codec_context,
            SwrContext** resample_context) {
            int error = 0;
            *resample_context = ffmpeg.swr_alloc_set_opts(null, ffmpeg.av_get_default_channel_layout(output_codec_context->channels),
            output_codec_context->sample_fmt, output_codec_context->sample_rate, ffmpeg.av_get_default_channel_layout(input_codec_context->channels),
            input_codec_context->sample_fmt, input_codec_context->sample_rate, 0, null);
            if (*resample_context == null) {
                Console.WriteLine("Could not allocate resample context");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            if ((error = ffmpeg.swr_init(*resample_context)) < 0) {
                Console.WriteLine("Could not open resample context");
                ffmpeg.swr_free(resample_context);
                return error;
            }
            return 0;
        }

        static unsafe int Init_fifo(AVAudioFifo** fifo, AVCodecContext* output_codec_context) {
            if ((*fifo = ffmpeg.av_audio_fifo_alloc(output_codec_context->sample_fmt, output_codec_context->channels, 1)) == null) {
                Console.WriteLine("Could not allocate FIFO");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }
            return 0;
        }

        static unsafe int read_decode_convert_and_store(AVAudioFifo* fifo, AVFormatContext* input_format_context, AVCodecContext* input_codec_context,
         AVCodecContext* output_codec_context, SwrContext* resampler_context) {
            int error = 0;
            byte** converted_input_samples = null;
            AVFrame* input_frame = ffmpeg.av_frame_alloc();
            if (input_frame == null) {
                Console.WriteLine("Could not allocate input frame");
                goto cleanup;
            }
            AVPacket* input_packet = ffmpeg.av_packet_alloc();
            if (input_packet == null) {
                Console.WriteLine("Could not allocate packet");
                goto cleanup;
            }
            if ((error = ffmpeg.av_read_frame(input_format_context, input_packet)) < 0) {

                Console.WriteLine($"Could not raed frame (eror '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }

            if ((error = ffmpeg.avcodec_send_packet(input_codec_context, input_packet)) < 0) {
                Console.WriteLine($"Could not send packet for decoding (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            if ((error = ffmpeg.avcodec_receive_frame(input_codec_context, input_frame)) < 0) {
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF) {
                    error = 0;
                    goto cleanup;
                }
                Console.WriteLine($"Could not decode frame (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            converted_input_samples = (byte**)ffmpeg.av_calloc((ulong)output_codec_context->ch_layout.nb_channels, (ulong)IntPtr.Size);
            if ((error = ffmpeg.av_samples_alloc(converted_input_samples, null, output_codec_context->ch_layout.nb_channels,
           input_frame->nb_samples, output_codec_context->sample_fmt, 0)) < 0) {
                Console.WriteLine($"Could not allocate converted input samples (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            if ((error = ffmpeg.swr_convert(resampler_context, converted_input_samples,
            input_frame->nb_samples,
            input_frame->extended_data, input_frame->nb_samples)) < 0) {
                Console.WriteLine($"Could not convert input samples (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            if ((error = ffmpeg.av_audio_fifo_realloc(fifo, ffmpeg.av_audio_fifo_size(fifo) + input_frame->nb_samples)) < 0) {
                Console.WriteLine("Could not reallocate FIFO");
                goto cleanup;
            }
            if (ffmpeg.av_audio_fifo_write(fifo, (void**)converted_input_samples, input_frame->nb_samples) < input_frame->nb_samples) {
                Console.WriteLine("Could not write data to FIFO");
                goto cleanup;
            }
        cleanup:
            if (converted_input_samples != null) {
                ffmpeg.av_freep(&converted_input_samples[0]);
                ffmpeg.av_free(converted_input_samples);
            }

            ffmpeg.av_frame_free(&input_frame);
            ffmpeg.av_packet_free(&input_packet);
            return error;

        }

        static unsafe int load_encode_and_write(AVAudioFifo* fifo, AVFormatContext* output_format_context, AVCodecContext* output_codec_context) {
            int error = 0;
            AVPacket* output_packet = ffmpeg.av_packet_alloc();
            AVFrame* output_frame = ffmpeg.av_frame_alloc();
            int frame_size = Math.Min(ffmpeg.av_audio_fifo_size(fifo), output_codec_context->frame_size);
            output_frame->nb_samples = frame_size;
            output_frame->ch_layout = output_codec_context->ch_layout;
            output_frame->format = (int)output_codec_context->sample_fmt;
            output_frame->sample_rate = output_codec_context->sample_rate;
            if ((error = ffmpeg.av_frame_get_buffer(output_frame, 0)) < 0) {
                Console.WriteLine($"Could not allocate output frame samples (error '{FFmpegHelper.av_err2str(error)}')");
                ffmpeg.av_frame_free(&output_frame);
                goto cleanup;
            }
            void* ptr = &output_frame->data;
            if (ffmpeg.av_audio_fifo_read(fifo, (void**)ptr, frame_size) < frame_size) {
                error = ffmpeg.AVERROR_EXIT;
                goto cleanup;
            }
            if (output_frame != null) {
                output_frame->pts = pts;
                pts += output_frame->nb_samples;
            }
            if ((error = ffmpeg.avcodec_send_frame(output_codec_context, output_frame)) < 0) {
                Console.WriteLine($"Could not send frame :{FFmpegHelper.av_err2str(error)}");
                goto cleanup;
            }
            if ((error = ffmpeg.avcodec_receive_packet(output_codec_context, output_packet)) < 0) {
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF) {
                    error = 0;
                    goto cleanup;
                }
                Console.WriteLine($"Could not receive packet :{FFmpegHelper.av_err2str(error)}");
                goto cleanup;
            }
            if ((error = ffmpeg.av_interleaved_write_frame(output_format_context, output_packet)) < 0) {
                Console.WriteLine($"Could not write frame (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
        cleanup:
            ffmpeg.av_packet_unref(output_packet);
            ffmpeg.av_frame_unref(output_frame);
            return error;
        }
    }
}
